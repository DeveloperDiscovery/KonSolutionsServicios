# Instala el servicio de Windows "KonSolutionsServicios". Ejecutalo como Administrador, desde la carpeta donde quedo publicado el .exe.
param([string]$Carpeta = $PSScriptRoot, [string]$Cuenta = "", [string]$Clave = "")
$ErrorActionPreference = "Stop"
$exe = Join-Path $Carpeta "KonSolutionsServicios.exe"
if (-not (Test-Path $exe)) { throw "No encuentro $exe. Publica primero (publicar.ps1) y corre este script desde la carpeta publicada." }
if (Get-Service "KonSolutionsServicios" -ErrorAction SilentlyContinue) { Stop-Service "KonSolutionsServicios" -ErrorAction SilentlyContinue; sc.exe delete "KonSolutionsServicios" | Out-Null; Start-Sleep 2 }
$args = @("create","KonSolutionsServicios","binPath= `"$exe`"","start= delayed-auto","DisplayName= KonSolutions Servicios")
if ($Cuenta) { $args += @("obj= $Cuenta","password= $Clave") }
sc.exe @args | Out-Null
sc.exe description KonSolutionsServicios "Servicio generico de KonSolutions: corre tareas programadas (hoy: carga de XML recibidos desde Microsoft 365)." | Out-Null
sc.exe failure KonSolutionsServicios reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Write-Host "Servicio creado. Antes de iniciarlo define a nivel de MAQUINA (y reinicia el servicio si ya corria):"
Write-Host "  [Environment]::SetEnvironmentVariable('KonSolutions_DB_CONNECTION','<cadena de conexion>','Machine')"
Write-Host "  [Environment]::SetEnvironmentVariable('KonSolutions_GRAPH_SECRET','<secreto de la app de Azure>','Machine')"
Write-Host "Completa TenantId, ClientId y Buzon en appsettings.json y luego: Start-Service KonSolutionsServicios"
