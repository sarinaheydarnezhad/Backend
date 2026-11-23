# Cent Google authentication

The previous Authorization Code + PKCE implementation has been removed. Google Identity Services is the only Google sign-in method. No Cent OAuth start/complete endpoint, client secret or redirect callback is used.

See [Cent authentication implementation report and deployment runbook](CENT-AUTHENTICATION.md) for both login methods, identity linking, account compatibility, exact settings, migration and verification results. Historical GoogleSubject records and the GIS challenge table are retained.
