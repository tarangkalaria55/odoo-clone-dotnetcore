$ErrorActionPreference = "Stop"

Set-Location $PSScriptRoot

Write-Host "==> Initializing Complete Modular Odoo .NET 10 Enterprise Suite..." -ForegroundColor Cyan

# -------------------------------------------------------------
# 9. BUILD, DEPLOY & RUN
# -------------------------------------------------------------
Write-Host "==> Registering Projects in Solution..." -ForegroundColor Cyan
dotnet sln add Core.OdooEngine HostApp ProductAddon InventoryAddon PurchaseAddon SalesAddon InvoicingAddon AccountingAddon

Write-Host "==> Compiling Solution (.NET 10)..." -ForegroundColor Cyan
dotnet build Core.OdooEngine -c Release
dotnet build ProductAddon -c Release
dotnet build InventoryAddon -c Release
dotnet build PurchaseAddon -c Release
dotnet build SalesAddon -c Release
dotnet build InvoicingAddon -c Release
dotnet build AccountingAddon -c Release

Write-Host "==> Deploying Addons to drop-in directories..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path `
    "HostApp/bin/Debug/net10.0/addons/ProductAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/InventoryAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/SalesAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/SalesAddon/wwwroot", `
    "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/AccountingAddon/views" | Out-Null

Copy-Item "HostApp/appsettings.json" "HostApp/bin/Debug/net10.0/" -Force

if (Test-Path "HostApp/bin/Debug/net10.0/installed_modules.json") {
    Remove-Item "HostApp/bin/Debug/net10.0/installed_modules.json" -Force
}

Copy-Item "ProductAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/ProductAddon/" -Force
Copy-Item "ProductAddon/bin/Release/net10.0/ProductAddon.dll" "HostApp/bin/Debug/net10.0/addons/ProductAddon/" -Force
Copy-Item "ProductAddon/views/product_views.xml" "HostApp/bin/Debug/net10.0/addons/ProductAddon/views/" -Force

Copy-Item "InventoryAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/InventoryAddon/" -Force
Copy-Item "InventoryAddon/bin/Release/net10.0/InventoryAddon.dll" "HostApp/bin/Debug/net10.0/addons/InventoryAddon/" -Force
Copy-Item "InventoryAddon/views/inventory_views.xml" "HostApp/bin/Debug/net10.0/addons/InventoryAddon/views/" -Force

Copy-Item "PurchaseAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/" -Force
Copy-Item "PurchaseAddon/bin/Release/net10.0/PurchaseAddon.dll" "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/" -Force
Copy-Item "PurchaseAddon/views/purchase_views.xml" "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/views/" -Force

Copy-Item "SalesAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/SalesAddon/" -Force
Copy-Item "SalesAddon/bin/Release/net10.0/SalesAddon.dll" "HostApp/bin/Debug/net10.0/addons/SalesAddon/" -Force
Copy-Item "SalesAddon/views/sales_views.xml" "HostApp/bin/Debug/net10.0/addons/SalesAddon/views/" -Force
Copy-Item "SalesAddon/wwwroot/sales_report.js" "HostApp/bin/Debug/net10.0/addons/SalesAddon/wwwroot/" -Force

Copy-Item "InvoicingAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/" -Force
Copy-Item "InvoicingAddon/bin/Release/net10.0/InvoicingAddon.dll" "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/" -Force
Copy-Item "InvoicingAddon/views/invoicing_views.xml" "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/views/" -Force

Copy-Item "AccountingAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/AccountingAddon/" -Force
Copy-Item "AccountingAddon/bin/Release/net10.0/AccountingAddon.dll" "HostApp/bin/Debug/net10.0/addons/AccountingAddon/" -Force
Copy-Item "AccountingAddon/views/accounting_views.xml" "HostApp/bin/Debug/net10.0/addons/AccountingAddon/views/" -Force

Write-Host "==> Launching HostApp on .NET 10 Ultimate ERP Suite..." -ForegroundColor Green
Set-Location HostApp
dotnet run