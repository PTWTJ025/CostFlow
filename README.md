# CostFlow — Procurement & Cost Analysis Ops (DEMO)

CostFlow is a full-stack SaaS-style enterprise web application for factory spare part procurement, automated price comparison, smart order key-in, and cost variance analysis.

It is structured as one cohesive product repository so reviewers and developers can inspect the MVC backend, Razor UI, MySQL database schema, Docker setup, and technical documentation in one place.

## Architecture

```text
ASP.NET Core MVC (Razor UI + Tailwind CSS)
  -> C# Controller & Service Layer
  -> Entity Framework Core (Pomelo)
  -> Aiven MySQL Database (Cloud)
  -> Apache Kafka Event Queue (Aiven Cloud)
```

## Tech Stack

| Layer | Technology |
| --- | --- |
| Frontend | ASP.NET Core Razor Views, Vanilla CSS / Tailwind CSS, JavaScript |
| Backend | ASP.NET Core 9 (C# MVC), ClosedXML (Excel Processing) |
| Database | MySQL (Aiven Cloud / Local MySQL Server) |
| ORM | Entity Framework Core (Pomelo.EntityFrameworkCore.MySql) |
| Auth | ASP.NET Core Identity (PBKDF2 Hashing, Cookie Authentication) |
| Integration | Apache Kafka (Confluent.Kafka) |
| DevOps | Docker, Render.com Cloud Deployment |

## Project Structure

```text
CostFlow/
├── Controllers/
│   ├── AccountController.cs       # Authentication & Cookie Management
│   ├── FileMergeController.cs     # Excel File Upload & Price Variance Matching
│   ├── ProductSearchController.cs # Smart AJAX Search & Batch Order Key-in
│   ├── PriceReferenceController.cs# Spare Part Master Price CRUD
│   └── ReportController.cs        # Historical Variance Reports & Excel Export
├── Models/
│   ├── DbEntities.cs              # Core Entities (Products, Orders, Sessions)
│   └── ApplicationUser.cs         # Extended User Profile & Employee ID
├── Services/
│   ├── ImportDbService.cs         # ClosedXML Excel Reader & Matching Engine
│   ├── ExportService.cs           # Excel Summary Generator
│   └── CustomUserClaimsPrincipalFactory.cs # Optimized Cookie Claims
├── Data/
│   └── AppDbContext.cs            # EF Core Database Context
├── Views/                         # Razor UI Views (.cshtml)
├── wwwroot/                       # Static Assets (CSS, JS, Images, JSON)
├── docs/                          # Technical Documentation & Guides
├── Dockerfile                     # Cloud Deployment Configuration
└── Program.cs                     # App Initialization & DI Setup
```

## Features

- **Automated Price Comparison (File Merge)**: Upload weekly production plans and approval sheets to automatically calculate price/quantity variance using ClosedXML.
- **Smart Order Key-in**: Fast spare part order entry with real-time AJAX auto-suggest for master prices and units, saved in batches.
- **Master Price Reference**: Full CRUD management for factory spare part reference prices (Admin only).
- **Executive Reports & Excel Export**: View historical variance reports and export factory-standard 10-column Excel sheets (`OrderExport_*.xlsx`) with auto-sum formulas.
- **Role-Based Authentication**: Secure ASP.NET Core Identity with PBKDF2 password hashing and Admin/Staff access control.
- **Cloud Ready**: Configured for Render.com Docker deployment, Aiven MySQL Cloud database, and Apache Kafka event integration.

## Run With Docker

```bash
docker build -t costflow-app .
docker run -d -p 8080:8080 -e ASPNETCORE_ENVIRONMENT=Production costflow-app
```

Services:

| Service | URL / Port |
| --- | --- |
| Web Application | http://localhost:8080 |
| MySQL Database | Configured via `ConnectionStrings__DefaultConnection` |

## Run Locally

```bash
# 1. Clone repository
git clone <repo-url>
cd CostFlow

# 2. Setup environment configuration
copy appsettings.example.json appsettings.json
# Edit appsettings.json with your local or Aiven MySQL connection string

# 3. Restore dependencies and Run
dotnet restore
dotnet run
```

Open your browser at `https://localhost:5001` or `http://localhost:5000`.

> **Note**: On first startup, the application automatically applies database migrations and seeds default test accounts.

## Default Accounts

| Employee Code | Password | Role |
| --- | --- | --- |
| `ADMIN01` | `admin1234` | Admin |
| `STAFF01` | `123456` | Staff |

## Useful Scripts

```bash
# Run application in development mode
dotnet run

# Build release bundle
dotnet build -c Release

# Publish self-contained or framework-dependent package
dotnet publish -c Release -o ./publish

# Add Entity Framework migration (if schema changes)
dotnet ef migrations add <MigrationName>

# Update database schema manually
dotnet ef database update
```

## Core Highlights & Workflow

| Feature Module | Key Controller / Action | Purpose |
| --- | --- | --- |
| Authentication | `AccountController` (`/Account/Login`) | Secure employee login & session management |
| File Merge | `FileMergeController` (`/FileMerge/Upload`) | Compare Excel files & calculate cost diff |
| Order Key-in | `ProductSearchController` (`/ProductSearch/Index`) | Smart AJAX key-in & batch saving |
| Excel Export | `ProductSearchController` (`/ProductSearch/ExportBatch`) | Generate 10-column factory Excel report |
| Price Reference | `PriceReferenceController` (`/PriceReference/Index`) | Manage reference spare part prices |
| Historical Reports | `ReportController` (`/Report/Index`) | Review past comparison reports |

## Portfolio Signals

- Full-stack enterprise MVC architecture
- Dockerized application ready for Render.com cloud deployment
- Cloud database integration (Aiven MySQL & Apache Kafka)
- ClosedXML Excel processing (Read & Write with formulas)
- Role-based security (ASP.NET Core Identity)
- AJAX real-time smart search & batch data entry
- Comprehensive architectural and feature-level documentation

## Docs

- [Project Structure & Architecture](docs/00_project_structure_and_architecture.md)
- [File Merge & Price Comparison](docs/01_file_merge.md)
- [Excel Export & Reports](docs/02_export_excel.md)
- [Spare Part Order Key-in](docs/03_purchase_order.md)
- [Price Reference Management](docs/04_price_reference.md)
- [Historical Reports](docs/05_reports.md)
