# Third-party notice — RADIUS protocol implementation

Substantial portions of the internal RADIUS protocol and cryptographic code under
`TechConnect.Server.Authentication.Radius` are derived from:

**Repository:** https://github.com/enteriaza/RadiusClient  
**Revision:** `fd274240b7bca26daead9d0e6edfd91cbaee6399` (`fd27424`)  
**Original package ID:** `SavageZA.Radius.Client`

The original work is licensed under the MIT License:

```text
MIT License

Copyright (c) 2026 Chris Knipe <cknipe@opticnetworks.net>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The code has been adapted for .NET 10, renamed into the
`TechConnect.Server.Authentication.Radius` namespace, trimmed to TechConnect’s
requirements, and compiled directly into `TechConnect.Server`. There is no
runtime dependency on the original RadiusClient project or NuGet package.

**Intentional protocol correction:** Access-Request Request Authenticators are
generated as 16 cryptographically random octets per RFC 2865 §3 / §4.1. The
original RadiusClient `AccessRequestAuthenticator` construction
`MD5(random ‖ shared-secret)` is **not** used.
