# Signing

Windows will not install an unsigned MSIX, so signing is part of every build,
not just of a release. Development uses a self-signed certificate; a release
uses a publicly issued one. The scripts are the same either way.

## The Publisher rule

`Identity/@Publisher` in `package\AppxManifest.xml` must equal the signing
certificate's `Subject` **character for character** — comma spacing, RDN order,
every field. A single space out of place produces:

```
signing certificate subject name (...) does not match
package manifest publisher (...)
```

The committed manifest carries a placeholder, never a real subject. A
certificate subject holds the holder's legal name and address; this repository
is public. Write it in at build time and take it out before committing:

```bat
build\get-cert-subject.cmd            :: lists certificates and thumbprints
build\set-publisher.cmd <thumbprint>  :: copies that certificate's subject in
build\set-publisher.cmd /revert       :: puts the placeholder back
```

`set-publisher.cmd` reads the subject from the certificate store, so no
personal data is ever typed into a file or into a command you might paste
somewhere. It refuses a certificate with no private key, an expired one, or one
without the Code Signing EKU, and it verifies bytewise that the manifest is
still CRLF afterwards.

## Changing certificate changes the package

The package family name is a hash of the Publisher string. A different
certificate is therefore a **different package**, and Windows will happily
register both — two entries in the context menu, both claiming the same CLSID,
one of them pointing at a stage directory that may no longer exist.

Unregister the old one first:

```bat
build\uninstall.cmd
```

`install.cmd` checks for this and refuses rather than producing the double
entry. Nothing in the tree hardcodes a family name, so nothing else needs
touching.

## What gets signed

`sign.cmd` signs the three binaries in `stage\` and then the `.msix`. Order
matters: a sparse package does not hash its external content, so signing the
package first would not be caught here — but it would be wrong for a full MSIX,
and the habit is worth keeping correct.

`sign-installer.cmd` signs the Inno setup `.exe` afterwards, because the
installer does not exist until it has packed the already-signed stage. That
file is the one users download, so its signature is what SmartScreen and the
UAC prompt show.

**The uninstaller embedded by Inno is not signed.** Inno signs it only through
its own `SignTool=` directive, which would mean putting a signing command line
inside `setup.iss`. Nothing signs it today; the file is written by Setup at
install time, and Windows does not prompt for it on removal.

Both scripts timestamp against DigiCert's RFC-3161 server. Override it for a
run by setting `TSURL` first:

```bat
set "TSURL=http://ts.ssl.com"
```

Without a timestamp every signature stops validating the day the certificate
expires, rather than the day it was made.

## Development certificate

```bat
build\make-dev-cert.cmd
```

Creates `CN=Jalyro Dev, O=Jalyro Dev, C=CH` in `Cert:\CurrentUser\My`, exports
`dev-cert.cer`, and prints the elevated `Import-Certificate` line that puts it
in `LocalMachine\TrustedPeople` — without that, `Add-AppxPackage` fails with
`0x800B0109`, an untrusted chain.

The subject deliberately carries `O=` and `C=` rather than a bare `CN=`, so the
Publisher-matching rule bites during development, while it is free, instead of
later against a paid certificate. It carries no `L=`: a locality is personal
data, and inventing one is worse than omitting it.

## Production certificate

A publicly issued code signing certificate chains to a root Windows already
trusts, so there is no `TrustedPeople` import and no `0x800B0109`.

Since June 2023 the CA/Browser Forum requires the private key to live on
hardware or in a certified HSM, so the certificate arrives either on a USB
token or through a cloud signing service. A cloud service loads the certificate
into `Cert:\CurrentUser\My` through a KSP, after which `signtool /sha1
<thumbprint>` works exactly as it does with a local key — that is why the
scripts take a thumbprint and never a `.pfx` path.

What differs from the development flow:

- The certificate must be loaded into the store before any script runs.
  `build\get-cert-subject.cmd` returning nothing means it is not loaded, not
  that it is missing.
- Signing may prompt. A cloud service configured for manual signing asks for
  credentials and a one-time password **per file**, which is five prompts for a
  full release: three binaries, the package, the installer.
- The subject usually carries more RDNs than the development one. Never retype
  it; `set-publisher.cmd` exists for this.

## Before committing

```bat
build\set-publisher.cmd /revert
```

Then confirm the working tree is clean — the manifest and the ffmpeg pin are
the two files that must go back to their committed state:

```bat
findstr /C:"Publisher=" package\AppxManifest.xml
git status --short
```
