<#
.SYNOPSIS
    Prueba el paquete que genera scripts/publicar.ps1 y su arranque en Production contra una base de produccion vacia.
.DESCRIPTION
    1. Publica en una carpeta temporal y revisa el paquete: sin archivos de configuracion local, sin secretos
       (se buscan los valores reales de .env y de panel/appsettings.Development.json, sin imprimirlos), con wwwroot,
       web.config y el ZIP con las entradas en la raiz.
    2. Crea una base temporal y la carga con scripts/desplegar-remoto.ps1 -Produccion (sin datos de prueba).
    3. Arranca la aplicacion PUBLICADA con ASPNETCORE_ENVIRONMENT=Production y HTTPS (certificado temporal), con la
       clave del administrador en Seed__AdminPassword (aleatoria, solo en memoria), e inicia sesion como admin.
    4. Recorre los GET sin parametros con esa sesion (la base no tiene pacientes: no debe haber errores 5xx),
       mide la memoria y comprueba que el registro no contiene claves.
    Al final detiene la aplicacion, borra la base y los archivos temporales.
    La clave de sa se lee de .env o de MSSQL_SA_PASSWORD; no se imprime.
.EXAMPLE
    ./tests/publicacion.ps1
#>
param(
    [string]$Servidor = 'localhost,1433',
    [string]$Usuario = 'sa',
    [string]$Base = 'VacunAppPubPrueba',
    [int]$Puerto = 5443
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$utf8 = New-Object Text.UTF8Encoding($false)

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
    $env:SQLCMDPASSWORD = $sa
    $r = sqlcmd -S $Servidor -d $BaseDatos -U $Usuario -N -C -b -h -1 -W -Q "SET NOCOUNT ON; $Consulta"
    if ($LASTEXITCODE -ne 0) { throw "Fallo la consulta: $Consulta" }
    $r
}

# Lee un registro que la aplicacion todavia tiene abierto para escritura.
function LeerRegistro([string]$Ruta) {
    if (-not (Test-Path $Ruta)) { return '' }
    $fs = New-Object IO.FileStream($Ruta, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { (New-Object IO.StreamReader($fs, $utf8)).ReadToEnd() } finally { $fs.Dispose() }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("vacunapp-pub-" + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tmp)
$pub = Join-Path $tmp 'panel'
$zip = "$pub.zip"
$proceso = $null
$variables = 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'ConnectionStrings__VacunApp', 'Seed__AdminPassword', 'Kestrel__Certificates__Default__Path', 'Kestrel__Certificates__Default__Password'

# Peticiones con curl.exe (PowerShell 5.1 no tiene -SkipCertificateCheck). Los cuerpos con claves van por archivo temporal.
$jar = Join-Path $tmp 'cookies.txt'
function Http([string]$Metodo, [string]$Ruta, [string]$Json = $null, [switch]$Sesion, [switch]$GuardaSesion) {
    $cab = Join-Path $tmp 'cab.txt'; $cuerpo = Join-Path $tmp 'cuerpo.txt'
    foreach ($f in $cab, $cuerpo) { if (Test-Path $f) { [IO.File]::Delete($f) } }
    $a = @('-s', '-k', '-X', $Metodo, '-D', $cab, '-o', $cuerpo, '-w', '%{http_code}', '--max-time', '60')
    if ($Sesion) { $a += @('-b', $jar) }
    if ($GuardaSesion) { $a += @('-c', $jar) }
    if ($Json) {
        $arch = Join-Path $tmp 'peticion.json'
        [IO.File]::WriteAllText($arch, $Json, $utf8)
        $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$arch")
    }
    $codigo = & curl.exe @a "https://localhost:$Puerto$Ruta"
    [pscustomobject]@{
        Codigo = [int]$codigo
        Cabeceras = $(if (Test-Path $cab) { [IO.File]::ReadAllText($cab, $utf8) } else { '' })
        Cuerpo = $(if (Test-Path $cuerpo) { [IO.File]::ReadAllText($cuerpo, $utf8) } else { '' })
    }
}

try {
    # ---------------------------------------------------------------- 1. Paquete
    Write-Host "> Publicando en $pub"
    $errorPub = ''
    try { $null = & (Join-Path $repo 'scripts\publicar.ps1') -Salida $pub -Zip *>&1 } catch { $errorPub = $_.Exception.Message }
    Caso 'publicar.ps1 termina sin error' ($errorPub -eq '') $errorPub

    foreach ($f in 'VacunApp.Panel.dll', 'web.config', 'appsettings.json', 'wwwroot\index.html', 'wwwroot\login.html', 'wwwroot\dashboard.js', 'wwwroot\vendor\chart.umd.js') {
        Caso "el paquete incluye $f" (Test-Path (Join-Path $pub $f))
    }
    $archivos = @(if (Test-Path $pub) { Get-ChildItem $pub -Recurse -File })
    $prohibidos = @($archivos | Where-Object { $_.Name -match '^appsettings\.(Development|Production)' -or $_.Name -match '\.example\.json$' -or $_.Name -eq '.env' -or $_.Extension -in '.pfx', '.pdb' })
    Caso 'el paquete no trae configuracion local, ejemplos, .env, .pfx ni .pdb' ($prohibidos.Count -eq 0) (($prohibidos | ForEach-Object { $_.Name }) -join ', ')

    # Valores secretos reales de esta maquina: se buscan en todo el paquete y no se imprimen.
    $secretos = New-Object System.Collections.Generic.List[string]
    $secretos.Add($sa)
    $dev = Join-Path $repo 'panel\appsettings.Development.json'
    if (Test-Path $dev) {
        $j = Get-Content $dev -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($v in $j.Seed.AdminPassword, $j.Seed.Password) { if ($v) { $secretos.Add([string]$v) } }
        if ($j.ConnectionStrings.VacunApp -match 'Password=([^;]+)') { $secretos.Add($Matches[1]) }
    }
    $conSecreto = New-Object System.Collections.Generic.List[string]
    foreach ($f in $archivos) {
        $texto = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($f.FullName))
        foreach ($s in $secretos) { if ($s.Length -ge 6 -and $texto.IndexOf($s, [StringComparison]::Ordinal) -ge 0) { $conSecreto.Add($f.Name); break } }
    }
    Caso "ningun archivo del paquete contiene las claves de esta maquina ($($secretos.Count) valores buscados)" ($conSecreto.Count -eq 0) ($conSecreto -join ', ')
    $conClave = @($archivos | Where-Object { $_.Extension -in '.json', '.config' } | Where-Object { (Get-Content $_.FullName -Raw) -match '(?i)password\s*[=:]\s*["'']?[^"''<\s;]{6,}' })
    Caso 'ni appsettings.json ni web.config traen claves ni cadenas de conexion' ($conClave.Count -eq 0) (($conClave | ForEach-Object { $_.Name }) -join ', ')

    Caso 'se genero el ZIP' (Test-Path $zip)
    if (Test-Path $zip) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $nombres = @($z.Entries | ForEach-Object { $_.FullName })
            Caso 'el ZIP tiene VacunApp.Panel.dll y web.config en la raiz' (($nombres -contains 'VacunApp.Panel.dll') -and ($nombres -contains 'web.config'))
            Caso 'el ZIP usa / como separador y no trae configuracion local' (-not ($nombres | Where-Object { $_ -match '\\' -or $_ -match 'appsettings\.(Development|Production)' }))
        } finally { $z.Dispose() }
    }

    # Una carpeta ajena con archivos no se vacia: solo se limpia publicar\ dentro del repositorio.
    $rechazo = ''
    try { $null = & (Join-Path $repo 'scripts\publicar.ps1') -Salida $pub *>&1 } catch { $rechazo = $_.Exception.Message }
    Caso 'publicar.ps1 se niega a escribir en una carpeta ajena que ya tiene archivos' ($rechazo -match 'ya tiene archivos') $rechazo
    Caso '... y deja intacto su contenido' (Test-Path (Join-Path $pub 'VacunApp.Panel.dll'))

    # ---------------------------------------------------------------- 2. Base de produccion vacia
    Write-Host "> Base temporal $Base cargada con -Produccion"
    $null = Sql "IF DB_ID(N'$Base') IS NOT NULL BEGIN ALTER DATABASE [$Base] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Base]; END; CREATE DATABASE [$Base] COLLATE Modern_Spanish_CI_AS;"
    $segura = ConvertTo-SecureString $sa -AsPlainText -Force
    $errorBd = ''
    try { $null = & (Join-Path $repo 'scripts\desplegar-remoto.ps1') -Produccion -Servidor $Servidor -Base $Base -Usuario $Usuario -Clave $segura *>&1 } catch { $errorBd = $_.Exception.Message }
    Caso 'la carga -Produccion termina sin error' ($errorBd -eq '') $errorBd

    # ---------------------------------------------------------------- 3. Arranque en Production
    Write-Host "> Arrancando la aplicacion publicada en Production (https://localhost:$Puerto)"
    $claveAdmin = 'Aa1!' + [Guid]::NewGuid().ToString('N').Substring(0, 16)
    $claveCert = [Guid]::NewGuid().ToString('N')
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $req = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=localhost', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = New-Object Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
    $san.AddDnsName('localhost')
    $req.CertificateExtensions.Add($san.Build())
    $cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(2))
    $pfx = Join-Path $tmp 'temporal.pfx'
    [IO.File]::WriteAllBytes($pfx, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $claveCert))

    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:ASPNETCORE_URLS = "https://localhost:$Puerto"
    $env:ConnectionStrings__VacunApp = "Server=$Servidor;Database=$Base;User Id=$Usuario;Password=$sa;TrustServerCertificate=True"
    $env:Seed__AdminPassword = $claveAdmin
    $env:Kestrel__Certificates__Default__Path = $pfx
    $env:Kestrel__Certificates__Default__Password = $claveCert
    $logOut = Join-Path $tmp 'salida.log'; $logErr = Join-Path $tmp 'errores.log'
    $proceso = Start-Process -FilePath 'dotnet' -ArgumentList 'VacunApp.Panel.dll' -WorkingDirectory $pub `
        -RedirectStandardOutput $logOut -RedirectStandardError $logErr -PassThru -WindowStyle Hidden
    foreach ($v in $variables) { [Environment]::SetEnvironmentVariable($v, $null) }
    $env:SQLCMDPASSWORD = $sa

    $listo = $false
    for ($i = 0; $i -lt 60 -and -not $listo; $i++) {
        if ($proceso.HasExited) { break }
        Start-Sleep -Milliseconds 500
        $listo = (& curl.exe -sk -o NUL -w '%{http_code}' --max-time 3 "https://localhost:$Puerto/login.html") -eq '200'
    }
    $logInicio = (LeerRegistro $logOut) + (LeerRegistro $logErr)
    Caso 'la aplicacion publicada arranca y sirve /login.html por HTTPS' $listo ("proceso terminado=$($proceso.HasExited); " + (($logInicio -split "`n" | Select-Object -Last 5) -join ' | '))

    if ($listo) {
        $r = Http GET '/login.html'
        Caso 'el login usa las cabeceras de seguridad (CSP, nosniff)' ($r.Cabeceras -match '(?i)content-security-policy:' -and $r.Cabeceras -match '(?i)x-content-type-options:\s*nosniff')
        $r = Http GET '/vendor/chart.umd.js'
        Caso 'Chart.js se sirve como JavaScript con charset utf-8' ($r.Codigo -eq 200 -and $r.Cabeceras -match '(?i)content-type:[^\r\n]*javascript[^\r\n]*utf-8')

        $r = Http GET '/api/yo'
        Caso '/api/yo sin sesion responde 401' ($r.Codigo -eq 401)

        $r = Http POST '/api/login' (@{ usuario = 'admin'; clave = 'Clave-incorrecta-1!' } | ConvertTo-Json) -GuardaSesion
        Caso 'login de admin con clave incorrecta responde 401' ($r.Codigo -eq 401 -and $r.Cuerpo -match 'Usuario o contrase')
        $r = Http POST '/api/login' (@{ usuario = 'epi01'; clave = $claveAdmin } | ConvertTo-Json) -GuardaSesion
        Caso 'las cuentas de demostracion no existen (epi01 responde 401)' ($r.Codigo -eq 401)

        $r = Http POST '/api/login' (@{ usuario = 'admin'; clave = $claveAdmin } | ConvertTo-Json) -GuardaSesion
        Caso 'login de admin con la clave de Seed__AdminPassword responde 200' ($r.Codigo -eq 200) "codigo=$($r.Codigo)"
        $cookie = ($r.Cabeceras -split "`r?`n" | Where-Object { $_ -match '(?i)^set-cookie:\s*\.VacunApp\.Auth=' } | Select-Object -First 1)
        Caso 'la cookie de sesion es Secure, HttpOnly y SameSite=Strict' ($cookie -match '(?i);\s*secure' -and $cookie -match '(?i);\s*httponly' -and $cookie -match '(?i);\s*samesite=strict') "$($cookie -replace '=[^;]+;', '=<valor>;')"

        $r = Http GET '/api/yo' -Sesion
        $yo = if ($r.Codigo -eq 200) { $r.Cuerpo | ConvertFrom-Json } else { $null }
        Caso '/api/yo con la cookie devuelve admin / ADMINISTRADOR' ($yo -and $yo.usuario -eq 'admin' -and $yo.rol -eq 'ADMINISTRADOR') "codigo=$($r.Codigo)"

        $principales = '/api/resumen', '/api/sarampion', '/api/cobertura', '/api/catalogos', '/api/dashboard', '/api/campanas', '/api/brotes', '/api/alertas', '/api/alertas/stock', '/api/pendientes', '/api/usuarios/'
        foreach ($ruta in $principales) {
            $r = Http GET $ruta -Sesion
            Caso "GET $ruta con la base vacia responde 200" ($r.Codigo -eq 200) "codigo=$($r.Codigo) $($r.Cuerpo.Substring(0, [Math]::Min(120, $r.Cuerpo.Length)))"
        }
        $r = Http GET '/api/stock' -Sesion
        Caso 'GET /api/stock (solo jefe de establecimiento) responde 403 al administrador' ($r.Codigo -eq 403) "codigo=$($r.Codigo)"
        foreach ($ruta in '/api/stock/alertas', '/api/horarios', '/api/citas', '/api/citas/dia', '/api/agenda/franjas', '/api/auditoria', '/api/mis-pacientes') {
            $r = Http GET $ruta -Sesion
            Caso "GET $ruta no produce error 5xx con la base vacia" ($r.Codigo -lt 500) "codigo=$($r.Codigo) $($r.Cuerpo.Substring(0, [Math]::Min(120, $r.Cuerpo.Length)))"
        }
        $r = Http GET '/api/dashboard' -Sesion
        $d = if ($r.Codigo -eq 200) { $r.Cuerpo | ConvertFrom-Json } else { $null }
        # vw_CoberturaDistrito agrupa desde los pacientes: un distrito sin elegibles no tiene denominador y no aparece.
        Caso 'el tablero de una base sin pacientes responde vacio y sin error (0 distritos, cobertura 0)' ($d -and $d.resumen.distritos -eq 0 -and $d.resumen.vacunados -eq 0 -and $d.resumen.coberturaRegional -eq 0 -and @($d.distritos).Count -eq 0) $(if ($d) { "distritos=$($d.resumen.distritos) vacunados=$($d.resumen.vacunados)" } else { "codigo=$($r.Codigo)" })
        $r = Http GET '/api/usuarios/' -Sesion
        $u = if ($r.Codigo -eq 200) { @($r.Cuerpo | ConvertFrom-Json) } else { @() }
        Caso 'la lista de usuarios tiene solo a admin' ($u.Count -eq 1 -and $u[0].nombreUsuario -eq 'admin') "filas=$($u.Count)"

        $p = Get-Process -Id $proceso.Id
        $ws = [Math]::Round($p.WorkingSet64 / 1MB, 1); $priv = [Math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Write-Host ("  memoria tras el recorrido: conjunto de trabajo {0} MB, privada {1} MB" -f $ws, $priv)
        Caso 'el conjunto de trabajo queda por debajo de los 256 MB del plan gratuito' ($ws -lt 256) "$ws MB"
    }

    $registros = (LeerRegistro $logOut) + (LeerRegistro $logErr)
    Caso 'el registro indica el entorno Production' ($registros -match 'Hosting environment: Production') (($registros -split "`n" | Select-Object -First 6) -join ' | ')
    Caso 'el registro no contiene claves (sa, administrador ni certificado)' (-not ($registros.Contains($sa) -or $registros.Contains($claveAdmin) -or $registros.Contains($claveCert)))
    Caso 'el registro no tiene excepciones sin controlar' ($registros -notmatch '(?i)unhandled exception|fail: Microsoft\.AspNetCore\.Diagnostics') (($registros -split "`n" | Where-Object { $_ -match 'fail:|Unhandled' } | Select-Object -First 3) -join ' | ')
}
finally {
    Write-Host '> Deteniendo la aplicacion y borrando lo temporal'
    foreach ($v in $variables) { [Environment]::SetEnvironmentVariable($v, $null) }
    if ($proceso -and -not $proceso.HasExited) { Stop-Process -Id $proceso.Id -Force; [void]$proceso.WaitForExit(10000) }
    $env:SQLCMDPASSWORD = $sa
    try { $null = Sql "IF DB_ID(N'$Base') IS NOT NULL BEGIN ALTER DATABASE [$Base] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Base]; END;" } catch { Write-Warning "No se pudo borrar $Base" }
    try { if (Test-Path $tmp) { [IO.Directory]::Delete($tmp, $true) } } catch { Write-Warning "No se pudo borrar $tmp" }
    [Environment]::SetEnvironmentVariable('SQLCMDPASSWORD', $null)
}

$malos = @($resultados | Where-Object { -not $_.Ok })
Write-Host ("`nResultado: {0} de {1} casos correctos" -f ($resultados.Count - $malos.Count), $resultados.Count)
if ($malos.Count -gt 0) { exit 1 }
