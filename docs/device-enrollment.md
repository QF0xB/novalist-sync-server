# Verified-device enrollment

Persistent mode accepts first-device enrollment after a fresh browser MFA login.
This flow works with bearer access tokens: it does not require the provider to
issue access-token `cnf.jkt` claims. The ordinary device and recovery device must
have independent Ed25519 signing and X25519 receiving keys.

1. The signed-in client requests `/devices/registration` and checks the canonical
   server URL and account ID before signing registrations.
2. It posts both public-key records to `/devices/enrollment`. The server binds a
   random, one-use nonce to that account and both complete public-key records.
   Challenges expire after five minutes, with eight per account and 1,024 total.
3. The client starts authorization-code sign-in with S256 PKCE, the returned
   nonce, `max_age=0`, and essential `amr` and `auth_time` ID-token claims.
4. It submits its ordinary and recovery possession signatures, the recovery
   linking signature, and `EnrollmentProof { challengeId, identityToken }` to
   `/devices/verified`, authenticated with its current API access token.
5. The server independently verifies the ID token against the configured
   provider's signing keys, issuer, native-client audience, account subject,
   nonce, expiry, and recent MFA. It consumes the challenge while holding the
   account enrollment lock, then atomically registers both devices.

The server receives public keys, the device association, signatures, and the
short-lived identity proof. It stores no ID token, refresh token, recovery
secret, private key, or project key. Challenges are in memory and do not survive
a restart. A replayed proof is rejected; after a lost successful response, the
client can retry ordinary registration without the consumed enrollment proof.

Configure `Authentication__NativeClient__ClientId` to the public desktop client
and the existing Bearer authority/audience settings for API access. The provider
must enforce MFA for that client's fresh authentication flow and issue truthful
`amr` (including `mfa`) and `auth_time` in its signed ID token. Enrollment rejects
missing or stale MFA claims; it does not infer MFA from a password login or an
arbitrary `acr` value. Browser reauthentication is separate from normal access
token refresh. Provider policy is responsible for requiring the factors again.

The desktop library's `HostedDeviceEnrollment.EnrollAsync` overloads accepting a
`HostedAccount` and browser callback/native browser perform this whole flow.
Callers must first save or show the recovery secret and supply its derived
identity. These are persistent-mode APIs; relay sessions use peer authentication
and do not require persistent verified-device registration.

Later devices require a signature from an active verified device or the saved
recovery identity. Losing every ordinary device does not reopen bearer-only
enrollment, and a revoked recovery identity cannot authorize a replacement.
Recovery of project content still requires its encryption keys.

The earlier `cnf.jkt` enrollment path remains available for callers that already
obtain a provider-signed, key-bound MFA access token. It is not the browser flow
and does not implement the full DPoP HTTP protocol.

Protocol references: [OIDC nonce, auth_time and max_age](https://openid.net/specs/openid-connect-core-1_0.html)
and [authentik key binding](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/key-binding/).
authentik's documented key binding applies to ID tokens; its access tokens remain
bearer tokens. This enrollment flow uses the standard signed nonce instead.
