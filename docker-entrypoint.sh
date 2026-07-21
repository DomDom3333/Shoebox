#!/bin/sh
set -e

mkdir -p /data/keys /data/pools
chown -R "${APP_UID:-1654}" /data

exec gosu "${APP_UID:-1654}" dotnet Shoebox.Web.dll "$@"
