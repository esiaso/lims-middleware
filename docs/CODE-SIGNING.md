# Code signing

Design 10.2 asks for a signed MSI. The CI job `windows-msi` signs the build when a certificate is configured, and
builds unsigned otherwise. Nothing else in the pipeline changes.

## What gets signed

With the certificate configured, `deploy/signing/sign.ps1` signs, with SHA-256 and an RFC 3161 timestamp:

* `Lims.*.exe` and `Lims.*.dll` in the service publish folder, before they go into the MSI;
* the MSI;
* `lims-capture.exe` and its `Lims.*.dll` files.

Each signature is checked straight after signing (status, signer thumbprint, timestamp). The bundle records the signer
in `bundle.json`, and `Install-Offline.ps1` refuses an MSI whose signature is broken or from another certificate.
Third-party files (the .NET runtime and NuGet packages) keep their publishers' signatures.

Every CI run also signs a copy of `Lims.Host.exe` with a throwaway self-signed certificate, so the script is known to
work before a real certificate exists.

## Which certificate

Since June 2023, publicly trusted code-signing certificates (OV and EV) are issued only with the private key held in
hardware (a token or a cloud HSM), so they cannot be exported as a PFX. That leaves two routes:

1. **A certificate from the hospital's own certificate authority, as a PFX.** This works with the CI setup as it is.
   The hospital's root certificate must be in the server's Trusted Root store (it usually is already, if the HTTPS
   certificate comes from the same authority). Windows only trusts it inside the hospital, which is where the
   software runs.
2. **A publicly trusted certificate held in a cloud signing service** (for example Azure Trusted Signing, DigiCert
   KeyLocker or SSL.com eSigner). `sign.ps1` would call that service's signtool plug-in instead of `/f` and `/p`.
   That is a small change, to be made once the provider is chosen, because each provider has its own credentials.

## Setting it up (route 1)

1. Request a code-signing certificate (Extended Key Usage "Code Signing", 1.3.6.1.5.5.7.3.3) with an exportable key,
   and export it as a PFX with a strong password.
2. In GitHub, go to repository Settings, then Secrets and variables, then Actions, and add:
   * `SIGNING_PFX_BASE64`: the PFX encoded as base64, for example from
     `[Convert]::ToBase64String([IO.File]::ReadAllBytes("sign.pfx")) | Set-Clipboard`;
   * `SIGNING_PFX_PASSWORD`: its password.
3. Delete the local PFX copy. The next CI run signs, and the `offline-bundle` artifact's `bundle.json` shows
   `"msi": true` with the signer.

The signing steps run only on pushes and on pull requests from branches of this repository: GitHub does not give
secrets to pull requests from forks.
