$result = Invoke-RestMethod -Uri 'https://skillhub-1388575217.cos.ap-guangzhou.myqcloud.com/install/skillhub.md'
$result | Out-File -FilePath 'C:\workshop\ai4c\kingcrab\src\OpenClaw.Gateway\install_skillhub.txt' -Encoding UTF8
Write-Host "Done"
