<#
.SYNOPSIS
    Prueba de concurrencia de vac.usp_ReservarCita (T4.2, RN-14 y RN-15).
.DESCRIPTION
    Abre N sesiones de sqlcmd (procesos y conexiones independientes) que esperan a la misma hora exacta
    (WAITFOR TIME) y reservan a la vez. Comprueba:
      A) N pacientes distintos compiten por una franja de 1 cupo   -> exactamente 1 aceptada, N-1 con 50126.
      B) 1 paciente reserva la misma dosis en N franjas distintas  -> exactamente 1 aceptada, N-1 con 50125.
      C) N pacientes compiten por una franja de 5 cupos            -> exactamente 5 aceptadas, N-5 con 50126.
    Y que en la base nunca queda más de una cita por cupo. Corre con 2, 10 y 20 sesiones (A, tres rondas cada una;
    B y C con 10 y 20). Los datos de prueba (DNI 8906xxxx) se borran al terminar. Sale con código 1 si algo falla.
    La contraseña de sa se toma de MSSQL_SA_PASSWORD o de .env, igual que desplegar.ps1; nunca se imprime.
.EXAMPLE
    ./tests/concurrencia.ps1
#>
param(
    [int[]]$Sesiones = (2, 10, 20),
    [int]$Rondas = 3,
    [string]$Servidor = 'localhost,1433'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$clave = $env:MSSQL_SA_PASSWORD
if (-not $clave -and (Test-Path (Join-Path $repo '.env'))) {
    $linea = Get-Content (Join-Path $repo '.env') | Where-Object { $_ -match '^\s*MSSQL_SA_PASSWORD\s*=' } | Select-Object -First 1
    if ($linea) { $clave = ($linea -replace '^\s*MSSQL_SA_PASSWORD\s*=\s*', '').Trim().Trim('"') }
}
if (-not $clave) { throw 'Defina MSSQL_SA_PASSWORD (variable de entorno o archivo .env).' }
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw 'No se encontró sqlcmd en el PATH.' }
$env:SQLCMDPASSWORD = $clave   # sqlcmd la lee de aquí; no viaja en la línea de comandos

function Invoke-Sql([string]$Consulta) {
    $salida = sqlcmd -S $Servidor -U sa -d VacunAppTacna -C -b -I -h -1 -W -Q "SET NOCOUNT ON; $Consulta"
    if ($LASTEXITCODE -ne 0) { throw "Falló la consulta: $salida" }
    $salida
}

$global:franjasCreadas = @()
$global:dniSiguiente = 0

# Borra solo lo que creó esta prueba: citas de los pacientes 8906xxxx, las franjas registradas y esos pacientes.
function Limpiar {
    $ids = if ($global:franjasCreadas.Count) { $global:franjasCreadas -join ',' } else { '-1' }
    Invoke-Sql @"
DELETE c FROM vac.Cita c JOIN vac.Paciente p ON p.IdPaciente = c.IdPaciente WHERE p.NumeroDocumento LIKE '8906%';
DELETE FROM vac.Cita WHERE IdHorario IN ($ids);
DELETE FROM vac.HorarioAtencion WHERE IdHorario IN ($ids);
DELETE FROM vac.Paciente WHERE NumeroDocumento LIKE '8906%';
"@ | Out-Null
    $global:franjasCreadas = @()
}

# Parámetros de la prueba: establecimiento, dosis y pacientes elegibles (20 meses: SPR dosis 1).
$est = [int](Invoke-Sql 'SELECT TOP (1) IdEstablecimiento FROM vac.EstablecimientoSalud ORDER BY IdEstablecimiento;' | Select-Object -First 1)
$esq = [int](Invoke-Sql "SELECT e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 1;" | Select-Object -First 1)
$vac = [int](Invoke-Sql "SELECT IdVacuna FROM vac.Vacuna WHERE Codigo = 'SPR';" | Select-Object -First 1)
$dist = [int](Invoke-Sql 'SELECT TOP (1) IdDistrito FROM vac.Distrito ORDER BY IdDistrito;' | Select-Object -First 1)

$global:franjaMinuto = Get-Random -Maximum 600   # minuto de partida al azar: una corrida interrumpida no deja franjas que choquen
function Nueva-Franja([int]$cupo) {
    $global:franjaMinuto++
    $id = [int](Invoke-Sql @"
INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo)
VALUES ($est, $vac, DATEADD(MINUTE, $($global:franjaMinuto), CAST(DATEADD(DAY, 20, CAST(GETDATE() AS DATE)) AS DATETIME2(0))), $cupo);
SELECT SCOPE_IDENTITY();
"@ | Select-Object -First 1)
    $global:franjasCreadas += $id
    $id
}

# Un paciente nuevo por sesión (DNI 8906 + contador): ninguno se reutiliza, así no chocan con RN-15 entre rondas.
function Nuevos-Pacientes([int]$n) {
    $ids = @()
    for ($i = 1; $i -le $n; $i++) {
        $global:dniSiguiente++
        $dni = '8906{0:D4}' -f $global:dniSiguiente
        $ids += [int](Invoke-Sql @"
INSERT vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, FechaNacimiento, Sexo, IdDistrito)
VALUES ('DNI', '$dni', 'Prueba', 'Concurrencia', DATEADD(MONTH, -20, CAST(GETDATE() AS DATE)), 'M', $dist);
SELECT SCOPE_IDENTITY();
"@ | Select-Object -First 1)
    }
    $ids
}

# Reserva en serie (sin competencia) y devuelve el IdCita, para preparar los escenarios de reprogramación.
function Reservar-Directo([int]$paciente, [int]$franja) {
    [int](Invoke-Sql "DECLARE @c INT; EXEC vac.usp_ReservarCita $paciente, $franja, $esq, NULL, @c OUTPUT; SELECT @c;" | Select-Object -First 1)
}

# Lanza una sesión por cada (paciente, franja), todas con la misma hora de disparo; devuelve el resultado de cada una.
function Disparar($pares) {
    $hora = (Get-Date).AddSeconds(8 + [math]::Ceiling($pares.Count * 0.25)).ToString('HH:mm:ss')
    $procesos = foreach ($p in $pares) {
        $exec = if ($p.Sql) { $p.Sql } else { "EXEC vac.usp_ReservarCita $($p.Paciente), $($p.Franja), $esq, NULL, @c OUTPUT" }
        $lote = "SET NOCOUNT ON; WAITFOR TIME '$hora'; BEGIN TRY DECLARE @c INT; $exec; SELECT 'RESULT=OK'; END TRY BEGIN CATCH SELECT CONCAT('RESULT=ERR', ERROR_NUMBER()); END CATCH"
        $psi = New-Object Diagnostics.ProcessStartInfo
        $psi.FileName = (Get-Command sqlcmd).Source
        $psi.Arguments = "-S $Servidor -U sa -d VacunAppTacna -C -I -h -1 -W -Q `"$lote`""
        $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
        $psi.EnvironmentVariables['SQLCMDPASSWORD'] = $clave
        [Diagnostics.Process]::Start($psi)
    }
    foreach ($pr in $procesos) {
        $texto = $pr.StandardOutput.ReadToEnd() + $pr.StandardError.ReadToEnd()
        $pr.WaitForExit()
        if ($texto -match 'RESULT=(\S+)') { $Matches[1] } else { "SIN-RESULTADO: $($texto.Trim())" }
    }
}

$fallos = 0
function Verificar([string]$nombre, $resultados, [int]$esperadosOk, [string]$codigoRechazo, [string]$sqlConteo, [int]$conteoEsperado) {
    $ok = @($resultados | Where-Object { $_ -eq 'OK' }).Count
    $rech = @($resultados | Where-Object { $_ -eq "ERR$codigoRechazo" }).Count
    $otros = @($resultados | Where-Object { $_ -ne 'OK' -and $_ -ne "ERR$codigoRechazo" })
    $enBase = [int](Invoke-Sql $sqlConteo | Select-Object -First 1)
    $bien = ($ok -eq $esperadosOk) -and ($rech -eq $resultados.Count - $esperadosOk) -and ($otros.Count -eq 0) -and ($enBase -eq $conteoEsperado)
    $estado = if ($bien) { 'OK   ' } else { 'FALLA' }
    Write-Host ("{0} {1}: aceptadas={2} (esperadas {3}), rechazadas {4}={5}, en base={6} (esperadas {7}){8}" -f $estado, $nombre, $ok, $esperadosOk, $codigoRechazo, $rech, $enBase, $conteoEsperado, $(if ($otros.Count) { " otros=" + ($otros -join ',') } else { '' }))
    if (-not $bien) { $script:fallos++ }
}

try {
    Limpiar
    foreach ($n in $Sesiones) {
        # A) N pacientes, una franja de 1 cupo
        for ($r = 1; $r -le $Rondas; $r++) {
            $franja = Nueva-Franja 1
            $pac = Nuevos-Pacientes $n
            $res = Disparar ($pac | ForEach-Object { @{ Paciente = $_; Franja = $franja } })
            Verificar "A  $n sesiones, 1 cupo, ronda $r" $res 1 '50126' "SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = $franja AND Estado <> 'CANCELADA';" 1
        }
        if ($n -ge 10) {
            # B) un mismo paciente y dosis en N franjas distintas
            $franjas = 1..$n | ForEach-Object { Nueva-Franja 1 }
            $pac = (Nuevos-Pacientes 1)[0]
            $res = Disparar ($franjas | ForEach-Object { @{ Paciente = $pac; Franja = $_ } })
            Verificar "B  $n sesiones, misma dosis, $n franjas" $res 1 '50125' "SELECT COUNT(*) FROM vac.Cita WHERE IdPaciente = $pac AND IdEsquema = $esq AND Estado = 'PROGRAMADA';" 1
            # C) N pacientes, una franja de 5 cupos
            $franja = Nueva-Franja 5
            $pac = Nuevos-Pacientes $n
            $res = Disparar ($pac | ForEach-Object { @{ Paciente = $_; Franja = $franja } })
            Verificar "C  $n sesiones, 5 cupos" $res 5 '50126' "SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = $franja AND Estado <> 'CANCELADA';" 5
        }
        # D) N citas, cada una en su franja, se reprograman a la vez a una franja de 1 cupo
        $destino = Nueva-Franja 1
        $citas = foreach ($p in (Nuevos-Pacientes $n)) { Reservar-Directo $p (Nueva-Franja 1) }
        $res = Disparar ($citas | ForEach-Object { @{ Sql = "EXEC vac.usp_ReprogramarCita $_, $destino" } })
        Verificar "D  $n reprogramaciones a 1 cupo" $res 1 '50126' "SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = $destino AND Estado <> 'CANCELADA';" 1
        Verificar "D' $n citas siguen programadas (ninguna se pierde)" @('OK') 1 '50126' "SELECT COUNT(*) FROM vac.Cita WHERE IdCita IN ($($citas -join ',')) AND Estado = 'PROGRAMADA';" $n

        # E) intercambios cruzados: A pasa de S1 a S2 mientras B pasa de S2 a S1 (cupo 2 en cada una).
        #    Con bloqueos en orden distinto aparecería un interbloqueo (error 1205); con orden ascendente, todos pasan.
        $pares = [math]::Floor($n / 2)
        $sentencias = @(); $franjasE = @(); $citasE = @()
        for ($i = 0; $i -lt $pares; $i++) {
            $s1 = Nueva-Franja 2; $s2 = Nueva-Franja 2; $franjasE += $s1, $s2
            $pa, $pb = Nuevos-Pacientes 2
            $ca = Reservar-Directo $pa $s1; $cb = Reservar-Directo $pb $s2; $citasE += $ca, $cb
            $sentencias += @{ Sql = "EXEC vac.usp_ReprogramarCita $ca, $s2" }, @{ Sql = "EXEC vac.usp_ReprogramarCita $cb, $s1" }
        }
        $res = Disparar $sentencias
        Verificar "E  $($pares * 2) intercambios cruzados" $res ($pares * 2) '50126' "SELECT COUNT(*) FROM vac.Cita WHERE IdHorario IN ($($franjasE -join ',')) AND Estado <> 'CANCELADA';" ($pares * 2)
        Limpiar
    }
}
finally {
    try { Limpiar } catch { Write-Warning "No se pudo limpiar: $_" }
    Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
}

if ($fallos -gt 0) { Write-Host "CONCURRENCIA: $fallos escenario(s) fallaron." -ForegroundColor Red; exit 1 }
Write-Host 'CONCURRENCIA: todos los escenarios correctos.' -ForegroundColor Green
exit 0
