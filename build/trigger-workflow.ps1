# 触发 GitHub Actions 云构建（由 push.csx 调用）
# 参数：-Token <PAT> -Repo <owner/name> -Ref <branch>
param(
    [Parameter(Mandatory = $true)][string]$Token,
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Ref
)

$ErrorActionPreference = 'Stop'
$headers = @{
    'Authorization' = "Bearer $Token"
    'Accept'        = 'application/vnd.github+json'
    'User-Agent'    = 'ColorMod-PushScript'
}

try {
    $uri = "https://api.github.com/repos/$Repo/actions/workflows/release.yml/dispatches"
    $body = @{ ref = $Ref } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers `
        -Body $body -ContentType 'application/json'
    Write-Output 'DISPATCH_OK'
}
catch {
    Write-Output ("DISPATCH_FAIL: " + $_.Exception.Message)
    exit 1
}