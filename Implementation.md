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
- **Ändring:** `Time = payload.Timestamp.ToUniversalTime()` i Ingest, och simulatorn
  skickar nu `DateTimeOffset.UtcNow` i stället för `DateTimeOffset.Now`.
- **Varför:** Ingest ska normalisera inkommande data. Tiden räknas om till UTC oavsett
  vilken tidszon sensorn använder, både för MQTT och HTTP.

### 7. CORS i Realtime.Hub
- **Fil:** `src/Realtime.Hub/Program.cs`
- **Ändring:** `http://localhost:5173` tillagd i `WithOrigins`.
- **Varför:** innovia-hubs React-frontend körs på port 5173. Utan den raden blockerar
  webbläsaren SignalR-anslutningen till hubben (same-origin policy).

## Ändringar i wu25-innoviahub
[phuusie/wu25-innoviahub](https://github.com/phuusie/wu25-innoviahub)

### 8. Hämta enheter från DeviceRegistry
- **Nya filer:**
  - `InnoviaHub.Api/Options/IotOptions.cs` – DeviceRegistrys adress och tenantens slug
  - `InnoviaHub.Shared/DTOs/Iot/DeviceDto.cs` – Id, Serial, Model, Status, RoomId
  - `InnoviaHub.Api/Services/Interfaces/IIotService.cs` + `Services/IotService.cs`
  - `InnoviaHub.Api/Controllers/IotController.cs` – `GET /api/Iot/devices` (bara admin)
  - `InnoviaHub.Api/Http/Iot.http`
- **Ändrade:** `appsettings.json` (sektionen `Iot`), `Program.cs` (`Configure<IotOptions>`),
  `ServiceCollectionExtensions.cs` (`AddHttpClient<IIotService, IotService>`)
- **Varför via backend:** DeviceRegistry saknar CORS, och anrop mellan servrar stoppas
  inte av CORS. Samma mönster som projektets övriga controllers och services.
- **Varför slug och inte tenant-GUID:** GUID:t ändras varje gång IoT-databasen töms,
  men sluggen `innovia` är stabil. Kostar ett extra anrop (by-slug) men kräver ingen
  uppdatering av appsettings.
- **Varför `AddHttpClient`:** .NET återanvänder anslutningarna i stället för att skapa
  en ny `HttpClient` per anrop.

### 9. Sensor-fliken visar riktiga enheter
- **Nya filer:** `InnoviaHub.Client/src/services/iotService.ts` – typen `Device` och `getDevices()`
- **Ändrade:** `InnoviaHub.Client/src/components/AdminSensors.tsx` – hämtar enheterna med
  `getDevices()` i stället för mockdatan i `adminData.ts`
- **Varför:** Admin-sidan hade redan en sensor-flik med påhittad data. Den används nu för
  de riktiga enheterna, så att IoT-delen blir en naturlig del av plattformen.
- **Kvar:** Larmräknaren i sidhuvudet räknar fortfarande mockdatan (ersätts i VG).

### 10. Live-värden via SignalR
- **Nya filer:** `InnoviaHub.Client/src/services/telemetryService.ts`
- **Ändrade:** `AdminSensors.tsx` (visar temperatur och CO₂ per enhet), `.env` (`VITE_IOT_HUB_URL`)
- **Hur:** Frontenden ansluter direkt till Realtime.Hub, går med i gruppen med
  `JoinTenant("innovia")` och lyssnar på `measurementReceived`. Värdena sparas per
  `deviceId` + `type`, eftersom temperatur och CO₂ kommer i separata meddelanden.
  `deviceId` matchar enhetens `Id` från DeviceRegistry.
- **Varför direkt till hubben:** Det är vad uppgiften beskriver, och projektet har redan
  samma mönster i `notificationService.ts`. Kräver en rad CORS i Realtime.Hub (steg 7).
- **Reconnect:** En ny anslutning får nytt `ConnectionId` och tappar gruppen, så
  `onreconnected` kör `JoinTenant` igen.
- **Cache:** Hubben skickar bara nya värden. Tjänsten sparar senaste värdet per enhet,
  så att värdena syns direkt när man byter tillbaka till fliken.

### 11. En gemensam .env
- **Ändrade:** `InnoviaHub.Client/vite.config.ts` (`envDir: '..'`), `.env` i roten
- **Ny fil:** `.env.example` – alla nycklar utan hemliga värden
- **Varför:** Docker- och Vite-inställningarna ligger nu i samma fil. Vite skickar bara
  `VITE_`-variabler till webbläsaren, så databaslösenordet stannar på servern.
  `.env.example` visar vilka variabler som behövs, eftersom `.env` inte är med i git.