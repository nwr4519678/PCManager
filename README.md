PCManager
=========

🖥️ PCManager - Nexus Command
A high-performance System Monitoring Dashboard built with .NET 10, Avalonia UI, and Silk.NET (OpenGL Shaders).


🚀 How to Run
Clone the repo: git clone [your-link]

Start Database: Run docker-compose up -d (Ensures SQL Server is ready).

Open Solution: Launch PCManager.sln in Visual Studio 2022.

Set Startup Projects: Set both PCManager.Backend and PCManager.UI to start.

Run (F5): Enjoy the real-time neon pulse graph!
Quickstart (development)

---
---


1. Restore and build

   dotnet restore
   dotnet build

2. Set your database connection string securely (do NOT commit credentials)

   - Option A: Environment variable (recommended)
     In PowerShell:
       $env:ConnectionStrings__Default = "Server=localhost,1433;Database=PCManagerDb;User Id=sa;Password=<your_password>;TrustServerCertificate=True;"

   - Option B: dotnet user-secrets (per developer)
     cd PCManager.Backend
     dotnet user-secrets init
     dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost,1433;Database=PCManagerDb;User Id=sa;Password=<your_password>;TrustServerCertificate=True;"

   - Option C: appsettings.Development.json (local only)
     Copy `PCManager.Backend/appsettings.Development.json.example` to `PCManager.Backend/appsettings.Development.json` and fill in values. Ensure `appsettings.Development.json` is not committed.

3. Run Backend and UI

   - From Visual Studio: set `PCManager.Backend` as Startup project and run. Then run the UI.
   - Or from terminal:
       dotnet run --project PCManager.Backend
       dotnet run --project PCManager.UI

Security notes

- Do NOT commit secrets (connection strings, passwords). Use environment variables or `dotnet user-secrets` during development and a secure secret store in production.
- Run `dotnet list package --vulnerable` to check for known vulnerable packages.

Diagnostics added

---
#Notes:
---
Ran command: `docker-compose up -d`
Listed directory PCManager
Listed directory PCManager.Backend
Viewed Program.cs:1-25
Listed directory PCManager.UI
Listed directory PCManager.Infrastructure
Listed directory Migrations

To run the **PC Manager** project, follow these steps in order. Since you have already successfully started the database container with Docker, you just need to initialize the database schema and start the services.

### 1. Initialize the Database
Before running the application, you need to apply the Entity Framework migrations to the SQL Server container you just started. Run this command from the root directory:

```powershell
dotnet ef database update --project PCManager.Infrastructure --startup-project PCManager.Backend
```

### 2. Run the Backend API
The backend handles real-time telemetry and system monitoring. Open a new terminal and run:

```powershell
dotnet run --project PCManager.Backend
```
*The backend will start and begin monitoring your system's CPU, GPU, and Network metrics.*

### 3. Run the UI (Dashboard)
Once the backend is running, you can start the Avalonia UI dashboard:

```powershell
dotnet run --project PCManager.UI
```

---

### Summary of Commands
| Step | Action | Command |
| :--- | :--- | :--- |
| **1** | **Database** | `docker-compose up -d` |
| **2** | **Migrations** | `dotnet ef database update --project PCManager.Infrastructure --startup-project PCManager.Backend` |
| **3** | **Backend** | `dotnet run --project PCManager.Backend` |
| **4** | **Frontend** | `dotnet run --project PCManager.UI` |

**Would you like me to run the database migrations for you now?**

- CORS ordering fixed and console startup message added in `Program.cs`.
- `OSService` CPU sampling improved to use `PerformanceCounter` on Windows and fallback logging.
- `MetricsController` and `TelemetryController` already log hits with `Console.WriteLine`.


