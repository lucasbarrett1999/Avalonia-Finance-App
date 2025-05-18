# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run Commands

```bash
# Restore dependencies
dotnet restore

# Build the application
dotnet build

# Run the application
dotnet run

# Build in release mode 
dotnet build -c Release

# Run Entity Framework migrations
dotnet ef migrations add MigrationName --project src/MyApp.Infrastructure
dotnet ef database update --project src/MyApp.Infrastructure

# Create a new migration
dotnet ef migrations add MigrationName --project src/MyApp.Infrastructure --startup-project .

# Apply migrations to the database
dotnet ef database update --project src/MyApp.Infrastructure --startup-project .
```

## Architecture Overview

This is an Avalonia-based desktop application built on .NET 8 with a clean architecture approach:

### Core Architecture Components

1. **Projects Structure**:
   - **MyApp**: Main application project with UI components and application logic
   - **MyApp.Core**: Core domain models, interfaces, and business logic
   - **MyApp.Infrastructure**: Data access, external services integration, and implementations

2. **UI Framework**:
   - Uses Avalonia UI 11.2.7 with MVVM pattern
   - ReactiveUI for reactive programming patterns

3. **Data Access**:
   - Entity Framework Core 9.0.4 with SQLite database
   - Repository pattern for data access abstraction
   - Unit of Work pattern for transaction management

4. **Dependencies**:
   - Uses Microsoft Dependency Injection (DI) container
   - Services are configured in `App.axaml.cs` in the `ConfigureServices` method

### Key Architecture Patterns

1. **MVVM Architecture**:
   - **Models**: Domain entities in `src/MyApp.Core/Entities/`
   - **ViewModels**: In `ViewModels/` folder, inheriting from `ViewModelBase`
   - **Views**: AXAML files in `Views/` folder with corresponding code-behind files

2. **Navigation**:
   - ReactiveUI routing via IScreen and IRoutableViewModel
   - MainWindowViewModel acts as the routing container

3. **External Service Integration**:
   - Plaid API integration for financial data via `PlaidService`
   - Configuration via `appsettings.json` and user secrets

4. **Database**:
   - SQLite database with Code-First EF Core approach
   - Migrations in `src/MyApp.Infrastructure/Migrations/`
   - Entities with relationships defined in `FinanceDbContext`

## Core Components

1. **Entities**:
   - `Account`: Represents a financial account
   - `Transaction`: Financial transactions linked to accounts
   - `Category`: Categories for transactions with hierarchical support

2. **ViewModels**:
   - `MainWindowViewModel`: Main container and navigation controller
   - Feature-specific ViewModels for each screen (Dashboard, Accounts, Budgets, Transactions, Goals)

3. **Services**:
   - `PlaidService`: Interface with the Plaid financial data API
   - Repository implementations for data access

4. **Database**: 
   - SQLite database configured in `appsettings.json`
   - Migrations are used to manage schema changes
   - Initial seeding in `DbInitializer`

## Common Development Workflows

1. **Adding a new entity**:
   - Add class to `src/MyApp.Core/Entities/`
   - Add DbSet to `FinanceDbContext` 
   - Configure relationships in `OnModelCreating`
   - Create a repository interface in `Core/Interfaces/`
   - Implement repository in `Infrastructure/Data/Repositories/`
   - Register in DI container in `App.axaml.cs`
   - Create migration and update database

2. **Adding a new screen**:
   - Create ViewModel in `ViewModels/`
   - Create View in `Views/` with `.axaml` and `.axaml.cs` files
   - Add navigation command in `MainWindowViewModel`
   - Register ViewModel in DI container in `App.axaml.cs`

3. **Theme handling**:
   - Application supports Light, Dark and Default (system) themes
   - Controlled via `MainWindowViewModel.SwitchTheme` method