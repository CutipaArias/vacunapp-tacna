<#
.SYNOPSIS
    Prueba el modo -Produccion de scripts/desplegar-remoto.ps1 contra una base local vacia (SQL Server de Docker).
.DESCRIPTION
    Crea una base temporal, carga esquema + catalogos + seguridad + stock + agenda + estadisticas con -Produccion y
    comprueba que NO hay datos de prueba, que solo existe el administrador (sin clave), que una segunda ejecucion se
    detiene sin tocar nada y que 12_produccion.sql se niega a correr sobre una base con pacientes. Al final borra la base.
    La clave de sa se lee de .env o de MSSQL_SA_PASSWORD; no se imprime.
.EXAMPLE
    ./tests/produccion.ps1
#>
param(
    [string]$Servidor = 'localhost,1433',
    [string]$Usuario = 'sa',
    [string]$Base = 'VacunAppProdPrueba',
    [string]$BaseReferencia = 'VacunAppTacna'   # base de desarrollo ya desplegada: sirve de referencia para contar objetos
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$sa = $env:MSSQL_SA_PASSWORD
if (-not $sa -and (Test-Path (Join-Path $repo '.env'))) {
    $linea = Get-Content (Join-Path $repo '.env') | Where-Object { $_ -match '^\s*MSSQL_SA_PASSWORD\s*=' } | Select-Object -First 1
    if ($linea) { $sa = ($linea -replace '^\s*MSSQL_SA_PASSWORD\s*=\s*', '').Trim().Trim('"') }
}
if (-not $sa) { throw 'Defina MSSQL_SA_PASSWORD (variable de entorno o archivo .env).' }
$env:SQLCMDPASSWORD = $sa

$resultados = New-Object System.Collections.Generic.List[object]
function Caso([string]$Nombre, [bool]$Ok, [string]$Detalle = '') {
    $resultados.Add([pscustomobject]@{ Ok = $Ok; Nombre = $Nombre; Detalle = $Detalle })
    Write-Host ("  {0} {1}{2}" -f $(if ($Ok) { '[OK]   ' } else { '[FALLA]' }), $Nombre, $(if (-not $Ok -and $Detalle) { " -> $Detalle" } else { '' }))
}
function Sql([string]$Consulta, [string]$BaseDatos = 'master') {
    $env:SQLCMDPASSWORD = $sa   # desplegar-remoto.ps1 la borra al terminar
    $r = sqlcmd -S $Servidor -d $BaseDatos -U $Usuario -N -C -b -h -1 -W -Q "SET NOCOUNT ON; $Consulta"
    if ($LASTEXITCODE -ne 0) { throw "Fallo la consulta: $Consulta" }
    $r
}
function Escalar([string]$Consulta, [string]$BaseDatos = $Base) { (@(Sql $Consulta $BaseDatos) | Select-Object -First 1).ToString().Trim() }
function Desplegar([hashtable]$Extra = @{}) {
    $segura = ConvertTo-SecureString $sa -AsPlainText -Force
    $salida = & (Join-Path $repo 'scripts\desplegar-remoto.ps1') @Extra -Servidor $Servidor -Base $Base -Usuario $Usuario -Clave $segura *>&1 | Out-String
    $salida
}
function EjecutarScript([string]$Archivo) {
    $env:SQLCMDPASSWORD = $sa
    $temporal = Join-Path ([IO.Path]::GetTempPath()) ("vacunapp-prueba-" + [Guid]::NewGuid().ToString('N') + '.sql')
    try {
        $utf8 = New-Object Text.UTF8Encoding($false)
        $sql = [IO.File]::ReadAllText((Join-Path $repo "database\$Archivo"), $utf8) -replace 'USE VacunAppTacna;', "USE [$Base];"
        [IO.File]::WriteAllText($temporal, $sql, $utf8)
        $null = sqlcmd -S $Servidor -d $Base -U $Usuario -N -C -b -I -f 65001 -W -i $temporal
        $LASTEXITCODE
    } finally { if (Test-Path $temporal) { [IO.File]::Delete($temporal) } }
}

try {
    Write-Host "> Base temporal $Base en $Servidor"
    $null = Sql "IF DB_ID(N'$Base') IS NOT NULL BEGIN ALTER DATABASE [$Base] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Base]; END; CREATE DATABASE [$Base] COLLATE Modern_Spanish_CI_AS;"

    Write-Host '> Casos de argumentos'
    $combinado = ''
    try { $null = Desplegar @{ Produccion = $true; Pruebas = $true }; $combinado = 'sin error' } catch { $combinado = $_.Exception.Message }
    Caso '-Produccion y -Pruebas no se pueden combinar' ($combinado -match 'combinar') $combinado
    Caso 'el rechazo no toco la base (sin esquema vac)' ((Escalar "SELECT COUNT(*) FROM sys.schemas WHERE name = N'vac'") -eq '0')

    Write-Host '> Carga en modo produccion'
    $error1 = ''
    try { $null = Desplegar @{ Produccion = $true } } catch { $error1 = $_.Exception.Message }
    Caso 'la carga termina sin error' ($error1 -eq '') $error1

    $tablas = Escalar "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('vac')"
    $tablasRef = Escalar "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('vac')" $BaseReferencia
    Caso "mismas tablas que la base de desarrollo ($tablasRef)" ($tablas -eq $tablasRef -and [int]$tablas -gt 0) "produccion=$tablas referencia=$tablasRef"
    $objetos = Escalar "SELECT COUNT(*) FROM sys.objects WHERE schema_id = SCHEMA_ID('vac') AND is_ms_shipped = 0"
    $objetosRef = Escalar "SELECT COUNT(*) FROM sys.objects WHERE schema_id = SCHEMA_ID('vac') AND is_ms_shipped = 0" $BaseReferencia
    Caso "mismos objetos que la base de desarrollo ($objetosRef)" ($objetos -eq $objetosRef) "produccion=$objetos referencia=$objetosRef"

    foreach ($t in 'Paciente', 'DosisAplicada', 'Alerta', 'Brote', 'Cita', 'HorarioAtencion', 'LoteVacuna', 'StockLote', 'MovimientoStock', 'Campana', 'CampanaDistrito', 'VinculoFamiliar') {
        $n = Escalar "SELECT COUNT(*) FROM vac.$t"
        Caso "vac.$t esta vacia (sin datos de prueba)" ($n -eq '0') "filas=$n"
    }
    foreach ($t in 'Provincia', 'Distrito', 'Enfermedad', 'Vacuna', 'EsquemaDosis', 'EstablecimientoSalud', 'Vacunador', 'Rol') {
        $n = Escalar "SELECT COUNT(*) FROM vac.$t"
        $nRef = Escalar "SELECT COUNT(*) FROM vac.$t" $BaseReferencia
        Caso "vac.$t trae el catalogo completo ($nRef)" ($n -eq $nRef -and [int]$n -gt 0) "produccion=$n referencia=$nRef"
    }

    $usuarios = @(Sql "SELECT u.NombreUsuario + '|' + r.Nombre + '|' + IIF(u.ClaveHash IS NULL, 'sin-clave', 'CON-CLAVE') + '|' + CAST(u.Activo AS VARCHAR(1)) FROM vac.Usuario u JOIN vac.Rol r ON r.IdRol = u.IdRol" $Base)
    Caso 'existe solo el administrador, activo y sin clave en la base' ($usuarios.Count -eq 1 -and $usuarios[0].Trim() -eq 'admin|ADMINISTRADOR|sin-clave|1') ($usuarios -join ' ; ')

    Write-Host '> Segunda ejecucion: debe detenerse sin tocar nada'
    $antes = Escalar "SELECT CONCAT((SELECT COUNT(*) FROM sys.objects), '/', CONVERT(VARCHAR(30), (SELECT MAX(modify_date) FROM sys.objects), 126), '/', (SELECT COUNT(*) FROM vac.Usuario))"
    $error2 = ''
    try { $null = Desplegar @{ Produccion = $true } } catch { $error2 = $_.Exception.Message }
    $despues = Escalar "SELECT CONCAT((SELECT COUNT(*) FROM sys.objects), '/', CONVERT(VARCHAR(30), (SELECT MAX(modify_date) FROM sys.objects), 126), '/', (SELECT COUNT(*) FROM vac.Usuario))"
    Caso 'la segunda ejecucion se rechaza porque ya existe el esquema' ($error2 -match 'ya contiene el esquema') $error2
    Caso 'la segunda ejecucion no modifico ningun objeto' ($antes -eq $despues) "$antes -> $despues"

    Write-Host '> 12_produccion.sql'
    Caso '12_produccion.sql se puede repetir sin error (idempotente)' ((EjecutarScript '12_produccion.sql') -eq 0)
    Caso 'sigue existiendo solo el administrador' ((Escalar 'SELECT COUNT(*) FROM vac.Usuario') -eq '1')

    # Una base con pacientes no es de produccion: el script se niega y no borra las cuentas.
    $null = Sql "INSERT vac.Usuario (NombreUsuario, NombreCompleto, IdRol) VALUES ('cuenta.prueba', 'Cuenta de prueba', 2);
                 INSERT vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, FechaNacimiento, Sexo, IdDistrito)
                 SELECT TOP (1) 'DNI', '89999999', 'Prueba', 'Guardia', '2025-01-01', 'M', IdDistrito FROM vac.Distrito;" $Base
    $codigo = EjecutarScript '12_produccion.sql'
    Caso '12_produccion.sql se niega a correr sobre una base con pacientes' ($codigo -ne 0) "codigo=$codigo"
    Caso 'y no borro la cuenta ni el paciente' ((Escalar "SELECT CONCAT((SELECT COUNT(*) FROM vac.Usuario WHERE NombreUsuario = 'cuenta.prueba'), (SELECT COUNT(*) FROM vac.Paciente))") -eq '11')
}
finally {
    Write-Host "> Borrando la base temporal $Base"
    try { $null = Sql "IF DB_ID(N'$Base') IS NOT NULL BEGIN ALTER DATABASE [$Base] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Base]; END;" } catch { Write-Warning "No se pudo borrar $Base" }
    Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
}

$malos = @($resultados | Where-Object { -not $_.Ok })
Write-Host ("`nResultado: {0} de {1} casos correctos" -f ($resultados.Count - $malos.Count), $resultados.Count)
if ($malos.Count -gt 0) { exit 1 }
