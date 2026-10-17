# Implementation

### 1. Startskript
- **Fil:** [start.ps1](start.ps1) (ny)
- **Varför:** README:n kräver fyra terminaler i rätt ordning. Skriptet startar Docker
  och tjänsterna, väntar på att varje tjänst svarar och bygger en gång så att filer inte låses.
- **Användning:**
    - Första gången:
      ```shell
      powershell -ExecutionPolicy Bypass -File ./start.ps1
      ```
    - När enheterna redan är registrerade (databasen har inte raderats):
      ```shell
      powershell -ExecutionPolicy Bypass -File ./start.ps1 -Simulator
      ```

### 2. Databasport 5432 → 5434
- **Filer:** `deploy/docker-compose.yml`, appsettings i DeviceRegistry och Ingest,
  reservsträngarna i `Program.cs` för Portal.Adapter och Rules.Engine
- **Varför:** En lokalt installerad PostgreSQL låg redan på 5432 och tog emot
  anslutningarna, så inloggningen misslyckades.

### 3. Registreringsskript
- **Fil:** [register-devices.ps1](register-devices.ps1) (ny)
- **Varför:** Databasen saknar volym och töms när containern skapas om, och
  DeviceRegistry stoppar inte dubbletter. Skriptet skapar tenant `innovia` och
  `dev-101`–`dev-110`, men kollar först om de redan finns, så det kan köras flera gånger.
- **Användning:**
    ```shell
    powershell -ExecutionPolicy Bypass -File ./register-devices.ps1
    ```

### 4. Cache-buggen i Ingest
- **Fil:** `src/Ingest.Gateway/Program.cs` (`DeviceRegistryClient.ResolveAsync`)
- **Problem:** Cachen hade bara `tenantSlug` som nyckel. Den första enheten som slogs upp
  efter start fick sitt GUID sparat för hela tenanten, så alla andra enheters mätvärden
  sparades på fel enhet, även oregistrerade serials.
- **Ändring:** Nyckeln är nu `$"{tenantSlug}/{deviceSerial}"`, både vid uppslag och sparande.
- **Varför:** Serial är bara unik inom en tenant, så nyckeln måste innehålla båda.

### 5. Simulatorn för 10 enheter
- **Fil:** `src/Edge.Simulator/Program.cs`
- **Problem:** Simulatorn skickade bara för en enhet (`dev-101`), som dessutom var hårdkodad på tre ställen.
- **Ändring:** En lista med `dev-101`–`dev-110` skapas före loopen, och en `foreach`
  skickar för varje enhet. `Task.Delay` är sänkt från 10 till 5 sekunder.
- **Varför:** Kravet är minst 10 enheter med data minst var 10:e sekund. Ett varv tar
  sändtiden plus väntan, så 10 s väntan hade blivit strax över gränsen.

### 6. Tidszon i Ingest
- **Fil:** `src/Ingest.Gateway/Program.cs` (`IngestService.ProcessAsync`)
- **Problem:** Simulatorn skickar lokal tid (`DateTimeOffset.Now`, +02:00). Npgsql accepterar
  bara UTC för `timestamp with time zone`, så alla MQTT-mätvärden kastades bort med ett fel.
- **Ändring:** `Time = payload.Timestamp.ToUniversalTime()`
- **Varför:** Ingest ska normalisera inkommande data. Tiden räknas om till UTC oavsett
  vilken tidszon sensorn använder, både för MQTT och HTTP.

### 7. CORS i Realtime.Hub
- **Fil:** `src/Realtime.Hub/Program.cs`
- **Ändring:** `http://localhost:5173` tillagd i `WithOrigins`.
- **Varför:** innovia-hubs React-frontend körs på port 5173. Utan den raden blockerar
  webbläsaren SignalR-anslutningen till hubben (same-origin policy).