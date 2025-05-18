#!/bin/bash

# Script to set the application environment and run the app
# Usage: ./scripts/set-environment.sh [Development|Staging|Production]

# Default to Development if no environment is specified
ENV=${1:-Development}

# Validate environment
if [[ "$ENV" != "Development" && "$ENV" != "Staging" && "$ENV" != "Production" ]]; then
  echo "Invalid environment: $ENV"
  echo "Valid options: Development, Staging, Production"
  exit 1
fi

echo "Setting environment to: $ENV"
export ASPNETCORE_ENVIRONMENT=$ENV

# Run the application
echo "Starting application in $ENV environment..."
dotnet run