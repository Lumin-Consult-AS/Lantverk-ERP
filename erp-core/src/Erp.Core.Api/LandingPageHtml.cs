namespace Erp.Core.Api;

internal static class LandingPageHtml
{
    public const string Content = """
        <!DOCTYPE html>
        <html lang="nb">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>ERP-plattform – Steg 1 (Kjerne-skjelett)</title>
          <style>
            :root { font-family: system-ui, -apple-system, sans-serif; color: #0f172a; background: #f8fafc; }
            body { max-width: 840px; margin: 2.5rem auto; padding: 0 1.25rem; }
            .card { background: #fff; border: 1px solid #e2e8f0; border-radius: 10px; padding: 1.5rem; margin-bottom: 1.25rem; box-shadow: 0 1px 2px rgba(0,0,0,0.04); }
            h1 { margin-top: 0; font-size: 1.5rem; }
            h2 { margin-top: 0; font-size: 1.15rem; }
            button { background: #0f172a; color: #fff; border: none; border-radius: 6px; padding: 0.6rem 1rem; cursor: pointer; font-weight: 500; margin-right: 0.5rem; }
            button.secondary { background: #475569; }
            pre { background: #0f172a; color: #e2e8f0; padding: 1rem; border-radius: 8px; overflow-x: auto; font-size: 0.85rem; }
            .badge { display: inline-block; padding: 0.2rem 0.6rem; border-radius: 999px; background: #e0f2fe; color: #0369a1; font-size: 0.8rem; font-weight: 600; }
            .empty-state { padding: 1.25rem; border: 1px dashed #cbd5e1; border-radius: 8px; text-align: center; color: #475569; margin: 1rem 0; }
          </style>
        </head>
        <body>
          <div class="card">
            <span class="badge">LUM-5 · Steg 1</span>
            <h1>ERP-kjerne – Innlogging og tenant-isolasjon</h1>
            <p>Logg inn med en demo-bruker via Keycloak OIDC, veksle eksternt JWT til kortlevd internt token, og inspiser det tomme, tenant-isolerte systemet.</p>
            <div>
              <button onclick="loginDemo('demo', 'demo123')">Logg inn: Jens Demo (Demo Handel)</button>
              <button class="secondary" onclick="loginDemo('anna', 'anna123')">Logg inn: Anna Nordisk (Nordisk Import)</button>
            </div>
          </div>

          <div id="session-card" class="card" style="display:none;">
            <h2>Aktiv sesjon</h2>
            <div class="empty-state" id="empty-apps-banner">
              Ingen apper er installert i denne tenanten ennå (tomt system).
            </div>
            <pre id="session-output"></pre>
          </div>

          <script>
            async function loginDemo(username, password) {
              const out = document.getElementById('session-output');
              const card = document.getElementById('session-card');
              card.style.display = 'block';
              out.textContent = 'Henter eksternt OIDC-token fra Keycloak...';
              try {
                const tokenRes = await fetch('http://localhost:8080/realms/erp-dev/protocol/openid-connect/token', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                  body: new URLSearchParams({
                    grant_type: 'password',
                    client_id: 'erp-web',
                    username,
                    password
                  })
                });
                if (!tokenRes.ok) throw new Error('Keycloak OIDC feilet: ' + tokenRes.status);
                const oidcData = await tokenRes.json();

                out.textContent = 'Veksler eksternt OIDC-token til internt kjerne-token...';
                const exchangeRes = await fetch('/api/v1/auth/token', {
                  method: 'POST',
                  headers: {
                    'Authorization': 'Bearer ' + oidcData.access_token,
                    'Content-Type': 'application/json'
                  },
                  body: JSON.stringify({})
                });
                if (!exchangeRes.ok) throw new Error('Token-utveksling feilet: ' + exchangeRes.status);
                const internalToken = await exchangeRes.json();

                const headers = { 'Authorization': 'Bearer ' + internalToken.accessToken };
                const [session, companies, countries, currencies] = await Promise.all([
                  fetch('/api/v1/session', { headers }).then(r => r.json()),
                  fetch('/api/v1/core/companies', { headers }).then(r => r.json()),
                  fetch('/api/v1/core/countries', { headers }).then(r => r.json()),
                  fetch('/api/v1/core/currencies', { headers }).then(r => r.json())
                ]);

                out.textContent = JSON.stringify({
                  tokenExchange: {
                    tenantId: internalToken.tenantId,
                    tenantName: internalToken.tenantName,
                    companyId: internalToken.companyId,
                    companyName: internalToken.companyName,
                    userId: internalToken.userId,
                    roles: internalToken.roles,
                    expiresInSeconds: internalToken.expiresIn
                  },
                  session,
                  companies,
                  countries,
                  currencies
                }, null, 2);
              } catch (err) {
                out.textContent = 'Feil: ' + err.message;
              }
            }
          </script>
        </body>
        </html>
        """;
}

