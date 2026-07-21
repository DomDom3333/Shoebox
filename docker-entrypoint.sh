#!/bin/sh
set -e

mkdir -p /data/keys
chown -R "${APP_UID:-1654}" /data

exec su-exec "${APP_UID:-1654}" dotnet Shoebox.Web.dll "$@"
