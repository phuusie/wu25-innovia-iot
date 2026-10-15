# register-devices.ps1 - creates tenant "innovia" and devices dev-101..dev-110
# Safe to run many times: it skips what already exists.
$base = 'http://localhost:5101'
$slug = 'innovia'

# 1. Tenant - look it up first, create it only if it is missing
try {
    $tenant = Invoke-RestMethod "$base/api/tenants/by-slug/$slug"
    Write-Host "Tenant '$slug' already exists"
} catch {
    $body = @{ name = 'Innovia Hub'; slug = $slug } | ConvertTo-Json
    $tenant = Invoke-RestMethod "$base/api/tenants" -Method Post -ContentType 'application/json' -Body $body
    Write-Host "Created tenant '$slug'" -ForegroundColor Green
}

# 2. Devices - same rule as the simulator: dev-101 ... dev-110
foreach ($i in 101..110) {
    $serial = "dev-$i";
    try {
        Invoke-RestMethod "$base/api/tenants/$($tenant.id)/devices/by-serial/$serial" | Out-Null
        Write-Host "  $serial already exists"
    } catch {
        $body = @{ model = 'Acme CO2-Temp'; serial = $serial; status = 'active' } | ConvertTo-Json
        Invoke-RestMethod "$base/api/tenants/$($tenant.id)/devices" -Method Post -ContentType 'application/json' -Body $body | Out-Null
        Write-Host "  Created $serial" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host "Tenant GUID: $($tenant.id)" -ForegroundColor Yellow