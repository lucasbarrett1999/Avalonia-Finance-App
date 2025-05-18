# MyApp - Financial Management Application

An Avalonia-based desktop application for financial management built with .NET 8, following clean architecture principles.

## Features

- Dashboard with financial overview
- Account management and synchronization via Plaid API
- Transaction tracking and categorization
- Budget planning and monitoring
- Financial goals setting and tracking

## Architecture

This application follows a clean architecture approach:

- **MyApp**: Main application project with UI components (Avalonia)
- **MyApp.Core**: Core domain models, interfaces, and business logic
- **MyApp.Infrastructure**: Data access, external services integration

### Technology Stack

- .NET 8
- Avalonia UI 11.2.7
- Entity Framework Core 9.0.4
- SQLite Database
- ReactiveUI for MVVM pattern

## Development Setup

### Prerequisites

- .NET 8 SDK
- Visual Studio 2022, JetBrains Rider, or Visual Studio Code

### Getting Started

1. Clone the repository
2. Restore dependencies: `dotnet restore`
3. Build the application: `dotnet build`
4. Run the application: `dotnet run`

### Database Migrations

- Create a new migration: `dotnet ef migrations add MigrationName --project src/MyApp.Infrastructure --startup-project .`
- Apply migrations: `dotnet ef database update --project src/MyApp.Infrastructure --startup-project .`

## Project Structure

- **ViewModels/**: MVVM ViewModels
- **Views/**: Avalonia UI Views
- **src/MyApp.Core/**: Domain models and interfaces
- **src/MyApp.Infrastructure/**: Implementations and data access
- **docs/**: Documentation for specific features