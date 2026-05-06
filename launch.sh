#!/bin/bash

# Configuration
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY_PATH="$PROJECT_DIR/UnoraLaunchpad/bin/Debug/net10.0/linux-x64/UnoraLaunchpad"
REQUIRED_DOTNET_MAJOR=10

echo "=== Unora Linux Launcher Bootstrapper ==="

# Function to check if a command exists
command_exists() {
    command -v "$1" >/dev/null 2>&1
}

echo "[*] Checking for updates..."
GIT_OUTPUT=$(git pull)
echo "[*] $GIT_OUTPUT"

# 1. Dependency Checks
echo "[*] Checking dependencies..."

# Check for required DLLs
if [ ! -f "UnoraLaunchpad/Resources/dawnd.dll" ] || [ ! -f "UnoraLaunchpad/Resources/ddraw.dll" ]; then
    echo "[!] Error: Required DLLs (dawnd.dll/ddraw.dll) not found in UnoraLaunchpad/Resources/"
    exit 1
fi

# Check dotnet
if ! command_exists dotnet; then
    echo "[!] Error: 'dotnet' is not installed. Please install .NET SDK 10.0."
    echo "[?] Visit: https://dotnet.microsoft.com/download/dotnet/10.0"
    echo "[?] Or try installing dotnet-sdk-10.0 via your package manager."
    exit 1
fi

# Check dotnet version
DOTNET_VERSION=$(dotnet --version)
DOTNET_MAJOR=$(echo "$DOTNET_VERSION" | cut -d. -f1)
if [ "$DOTNET_MAJOR" -lt "$REQUIRED_DOTNET_MAJOR" ]; then
    echo "[!] Error: .NET version $DOTNET_VERSION detected. Version $REQUIRED_DOTNET_MAJOR.x is required."
    echo "[?] Try installing dotnet-sdk-10.0 via your package manager."
    exit 1
else
    echo "[+] .NET SDK $DOTNET_VERSION detected."
fi

# Check Lutris (Optional but recommended)
if ! command_exists lutris; then
    if command_exists flatpak && flatpak list | grep -q "net.lutris.Lutris"; then
        echo "[+] Lutris detected via Flatpak."
    else
        echo "[!] Warning: 'lutris' not found. Lutris-based launching will be unavailable."
    fi
fi

# Check Wine (Optional but recommended)
if ! command_exists wine; then
    echo "[!] Warning: 'wine' not found. Direct launching might fail if not using Lutris."
fi

# 2. Check ptrace permissions
# Required for memory patching in RuntimePatcher.cs
PTRACE_SCOPE_FILE="/proc/sys/kernel/yama/ptrace_scope"
if [ -f "$PTRACE_SCOPE_FILE" ]; then
    PTRACE_SCOPE=$(cat "$PTRACE_SCOPE_FILE")
    if [ "$PTRACE_SCOPE" != "0" ]; then
        echo "[!] Warning: ptrace_scope is restricted ($PTRACE_SCOPE)."
        echo "[!] Memory patching requires ptrace_scope = 0."
        echo "[?] Attempting to enable (requires sudo)..."
        if ! echo 0 | sudo tee "$PTRACE_SCOPE_FILE" > /dev/null; then
            echo "[!] Failed to set ptrace_scope. Launcher may not be able to patch the game memory."
            echo "[?] You can try manually: echo 0 | sudo tee /proc/sys/kernel/yama/ptrace_scope"
        fi
    fi
else
    echo "[*] ptrace_scope file not found, skipping ptrace check (likely not on a Yama-protected system)."
fi

# 3. Build the project
echo "[*] Building project..."
cd "$PROJECT_DIR" || exit 1

# Ensure LauncherSettings and Resources directories exist so they can be copied/referenced
mkdir -p UnoraLaunchpad/LauncherSettings
mkdir -p "$(dirname "$BINARY_PATH")/Resources"

if ! dotnet build UnoraLaunchpad/UnoraLaunchpad.csproj -r linux-x64 -v quiet; then
    echo "[!] Build failed. Please check the errors above."
    exit 1
fi

# Copy Resources to the binary directory
cp -r UnoraLaunchpad/Resources/* "$(dirname "$BINARY_PATH")/Resources/"

# 3.5 Sync DLLs to game directory
echo "[*] Syncing resources to game directory..."
SETTINGS_FILE="UnoraLaunchpad/LauncherSettings/settings.json"
GAME_PATH=""

if [ -f "$SETTINGS_FILE" ]; then
    # Simple extraction of GamePath from settings.json
    GAME_PATH=$(sed -n 's/.*"GamePath": "\(.*\)".*/\1/p' "$SETTINGS_FILE")
fi

# Fallback auto-discovery for common Linux paths if settings is empty or invalid
if [ -z "$GAME_PATH" ] || [ ! -d "$GAME_PATH" ]; then
    for DP in "$HOME/Games/dark-ages--1/drive_c/Program Files/Dark Ages" "$HOME/Games/dark-ages/drive_c/Program Files/Dark Ages"; do
        if [ -d "$DP" ]; then
            GAME_PATH="$DP"
            break
        fi
    done
fi

if [ -d "$GAME_PATH" ]; then
    echo "[+] Found game directory: $GAME_PATH"
    cp "UnoraLaunchpad/Resources/dawnd.dll" "$GAME_PATH/"
    cp "UnoraLaunchpad/Resources/ddraw.dll" "$GAME_PATH/"
    echo "[+] DLLs copied to game folder."
else
    echo "[!] Warning: Could not find game directory to sync DLLs. Launcher will attempt discovery at runtime."
fi

# 4. Run the binary
if [ -f "$BINARY_PATH" ]; then
    echo "[*] Launching Unora..."
    # Set the working directory to the binary's directory so it finds its resources
    cd "$(dirname "$BINARY_PATH")" || exit 1
    ./UnoraLaunchpad "$@"
else
    echo "[!] Error: Binary not found at $BINARY_PATH"
    exit 1
fi
