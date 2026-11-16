# Signs the installer for the in-app updater: writes <installer>.sig, a base64 RSA-SHA256 (PKCS #1 v1.5) signature.
# The private key (RSA XML, as made by RSACryptoServiceProvider.ToXmlString($true)) comes from the
# UPDATE_SIGNING_KEY environment variable. The app checks the signature against the public key in Updater.cs.
param([Parameter(Mandatory = $true)][string]$Installer)
$ErrorActionPreference = "Stop"

if (-not $env:UPDATE_SIGNING_KEY) { throw "UPDATE_SIGNING_KEY is not set; releases must be signed for the in-app updater." }
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.FromXmlString($env:UPDATE_SIGNING_KEY)
    $data = [System.IO.File]::ReadAllBytes($Installer)
    $signature = $rsa.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    if (-not $rsa.VerifyData($data, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw "Signature self-check failed." }
}
finally { $rsa.Dispose() }
[System.IO.File]::WriteAllText("$Installer.sig", [Convert]::ToBase64String($signature))
Write-Host "Signed $Installer"
