# Calendar-MCP nix-config Integration Design

## Overview

Integrate calendar-mcp into ~/nix-config for automatic deployment on ultraviolet with CloudFlare Tunnel access and persistent Google/Office authentication.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      ultraviolet                            │
├─────────────────────────────────────────────────────────────┤
│  calendar-mcp.husbuddies.gay                                │
│         │                                                   │
│         ▼                                                   │
│  CloudFlare Tunnel (existing) ──► CloudFlare Access (OIDC)  │
│         │                                                   │
│         ▼                                                   │
│  CalendarMcp.HttpServer (port 8001)                         │
│         │                                                   │
│         ▼                                                   │
│  /var/lib/calendar-mcp/                                     │
│    ├── appsettings.json (nix-generated)                     │
│    ├── msal_cache_*.bin (M365 tokens)                       │
│    └── google/*/... (Google tokens)                         │
└─────────────────────────────────────────────────────────────┘

Secrets (agenix):
  - calendar-mcp-cf-client-id
  - calendar-mcp-cf-client-secret
  - calendar-mcp-google-secret
```

## Configuration

| Component | Details |
|-----------|---------|
| **Host** | ultraviolet |
| **Port** | 8001 |
| **URL** | calendar-mcp.husbuddies.gay |
| **Transport** | HTTP with CloudFlare Access OAuth |
| **Data dir** | /var/lib/calendar-mcp |
| **Backup** | Daily at 3:15 AM → /mnt/backups/calendar-mcp/ |

## Files to Create/Modify

### In ~/nix-config

```
secrets/
├── secrets.nix                              # Add new secret declarations
└── hosts/ultraviolet/
    ├── calendar-mcp-cf-client-id.age        # CloudFlare Access client ID
    ├── calendar-mcp-cf-client-secret.age    # CloudFlare Access client secret
    └── calendar-mcp-google-secret.age       # Google OAuth client secret

hosts/ultraviolet/
├── default.nix                              # Add import for calendar-mcp
└── services/
    └── calendar-mcp.nix                     # New service configuration

flake.nix                                    # Add calendar-mcp input
```

### flake.nix Addition

```nix
inputs.calendar-mcp = {
  url = "github:joshsymonds/calendar-mcp";  # or local path
  inputs.nixpkgs.follows = "nixpkgs";
};
```

### secrets/secrets.nix Additions

```nix
"secrets/hosts/ultraviolet/calendar-mcp-cf-client-id.age".publicKeys = keys.ultraviolet;
"secrets/hosts/ultraviolet/calendar-mcp-cf-client-secret.age".publicKeys = keys.ultraviolet;
"secrets/hosts/ultraviolet/calendar-mcp-google-secret.age".publicKeys = keys.ultraviolet;
```

### Service Configuration (calendar-mcp.nix)

```nix
{ config, pkgs, inputs, ... }:
let
  backupScript = pkgs.writeShellScript "backup-calendar-mcp" ''
    set -euo pipefail

    SOURCE_DIR="/var/lib/calendar-mcp"
    BACKUP_BASE="/mnt/backups/calendar-mcp"
    TIMESTAMP=$(date +%Y%m%d-%H%M%S)
    BACKUP_PATH="$BACKUP_BASE/$TIMESTAMP"

    if ! mountpoint -q /mnt/backups; then
      echo "Error: /mnt/backups is not mounted"
      exit 1
    fi

    mkdir -p "$BACKUP_BASE"

    rsync -rlptDv --delete \
      "$SOURCE_DIR/" "$BACKUP_PATH/"

    ln -sfn "$BACKUP_PATH" "$BACKUP_BASE/latest"

    # Keep 14 days of backups
    find "$BACKUP_BASE" -maxdepth 1 -type d -name "20*" -mtime +14 -exec rm -rf {} \; 2>/dev/null || true

    logger -t calendar-mcp-backup "Backup completed: $TIMESTAMP"
  '';
in
{
  age.secrets = {
    "calendar-mcp-cf-client-id" = {
      file = ../../../secrets/hosts/ultraviolet/calendar-mcp-cf-client-id.age;
      owner = "calendar-mcp";
      group = "calendar-mcp";
      mode = "0400";
    };
    "calendar-mcp-cf-client-secret" = {
      file = ../../../secrets/hosts/ultraviolet/calendar-mcp-cf-client-secret.age;
      owner = "calendar-mcp";
      group = "calendar-mcp";
      mode = "0400";
    };
    "calendar-mcp-google-secret" = {
      file = ../../../secrets/hosts/ultraviolet/calendar-mcp-google-secret.age;
      owner = "calendar-mcp";
      group = "calendar-mcp";
      mode = "0400";
    };
  };

  services.calendar-mcp = {
    enable = true;
    transport = "http";
    host = "127.0.0.1";
    port = 8001;
    accessClientIdFile = config.age.secrets."calendar-mcp-cf-client-id".path;
    accessClientSecretFile = config.age.secrets."calendar-mcp-cf-client-secret".path;
    accessConfigUrl = "https://<team>.cloudflareaccess.com/cdn-cgi/access/sso/oidc/<app-id>/.well-known/openid-configuration";
  };

  # Backup service
  systemd.services.calendar-mcp-backup = {
    description = "Calendar MCP backup";
    serviceConfig = {
      Type = "oneshot";
      ExecStart = "${backupScript}";
    };
  };

  # Daily backup timer at 3:15 AM
  systemd.timers.calendar-mcp-backup = {
    description = "Daily Calendar MCP backup timer";
    wantedBy = ["timers.target"];
    timerConfig = {
      OnCalendar = "*-*-* 03:15:00";
      Persistent = true;
      RandomizedDelaySec = "5m";
    };
  };
}
```

## OAuth App Setup

### Microsoft (Azure AD)

1. Go to Azure Portal → App Registrations → New Registration
2. Name: "Calendar MCP"
3. Supported account types: Accounts in any organizational directory and personal Microsoft accounts
4. Redirect URI: Public client/native, `http://localhost`
5. API Permissions → Add delegated permissions:
   - `Mail.Read`
   - `Mail.Send`
   - `Calendars.ReadWrite`
   - `User.Read`
6. Note the **Application (client) ID** and **Directory (tenant) ID**

### Google (Cloud Console)

1. Go to Cloud Console → APIs & Services → Credentials
2. Create OAuth 2.0 Client ID (Desktop app)
3. Enable APIs: Gmail API, Google Calendar API
4. OAuth consent screen → Add scopes:
   - `https://www.googleapis.com/auth/gmail.readonly`
   - `https://www.googleapis.com/auth/gmail.send`
   - `https://www.googleapis.com/auth/calendar.readonly`
   - `https://www.googleapis.com/auth/calendar.events`
5. Note the **Client ID** and **Client Secret**

### CloudFlare Access

1. Zero Trust Dashboard → Access → Applications → Add Application
2. Self-hosted application
3. Application domain: `calendar-mcp.husbuddies.gay`
4. Configure identity providers as needed
5. Create policy for allowed users
6. Note the **Client ID**, **Client Secret**, and **OIDC Config URL**

## Initial Setup Process

### 1. Create OAuth Apps (in browser)
- Microsoft Azure AD app
- Google Cloud OAuth app
- CloudFlare Access application

### 2. Create Agenix Secrets (local machine)
```bash
cd ~/nix-config
echo "cf-client-id" | agenix -e secrets/hosts/ultraviolet/calendar-mcp-cf-client-id.age
echo "cf-client-secret" | agenix -e secrets/hosts/ultraviolet/calendar-mcp-cf-client-secret.age
echo "google-client-secret" | agenix -e secrets/hosts/ultraviolet/calendar-mcp-google-secret.age
```

### 3. Deploy
```bash
cd ~/nix-config
nixos-rebuild switch --flake .#ultraviolet --target-host ultraviolet
```

### 4. Add Accounts (SSH to ultraviolet)
```bash
# Add Microsoft 365 account
calendar-mcp-cli add-m365-account --device-code
# Follow the device code flow in browser

# Add Google account
calendar-mcp-cli add-google-account --device-code
# Follow the device code flow in browser

# Verify accounts work
calendar-mcp-cli list-accounts
calendar-mcp-cli test-account <account-id>
```

### 5. Add CloudFlare Tunnel Route
In CloudFlare Zero Trust Dashboard → Networks → Tunnels → ultraviolet tunnel:
- Add public hostname: `calendar-mcp.husbuddies.gay`
- Service: `http://localhost:8001`

## Backup & Restore

### Backup Location
`/mnt/backups/calendar-mcp/`

### Manual Backup
```bash
sudo systemctl start calendar-mcp-backup.service
```

### Check Backup Status
```bash
systemctl status calendar-mcp-backup.timer
ls -la /mnt/backups/calendar-mcp/
```

### Restore
```bash
# Stop service
sudo systemctl stop calendar-mcp

# Restore from backup
sudo rsync -av --delete /mnt/backups/calendar-mcp/latest/ /var/lib/calendar-mcp/

# Start service
sudo systemctl start calendar-mcp
```

## Verification

After deployment, verify:

1. **Service running:**
   ```bash
   systemctl status calendar-mcp
   ```

2. **Health check:**
   ```bash
   curl http://localhost:8001/health
   ```

3. **CloudFlare Access working:**
   Visit https://calendar-mcp.husbuddies.gay - should redirect to CF Access login

4. **Backup timer active:**
   ```bash
   systemctl list-timers | grep calendar-mcp
   ```
