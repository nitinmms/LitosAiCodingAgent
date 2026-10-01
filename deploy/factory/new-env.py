"""Creates .env.factory at the repository root with freshly generated passwords.

Usage: python deploy/factory/new-env.py

It never prints a password and never overwrites an existing file: the database keeps the
passwords it was first created with, so regenerating them would lock the host out.
"""
import os
import secrets
import sys

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
path = os.path.join(root, ".env.factory")

if os.path.exists(path):
    print(".env.factory already exists; leaving it unchanged.")
    sys.exit(0)

admin, migrator, runtime, app_admin = (secrets.token_urlsafe(24) for _ in range(4))
base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/.local/share")
data_dir = os.path.join(base, "LitosSoftwareFactory").replace(os.sep, "/")
connection = "Host=127.0.0.1;Port=5433;Database=litos_factory"

with open(path, "w", newline="\n") as file:
    file.write(
        f"FACTORY_DB_ADMIN_PASSWORD={admin}\n"
        f"FACTORY_DB_MIGRATOR_PASSWORD={migrator}\n"
        f"FACTORY_DB_RUNTIME_PASSWORD={runtime}\n"
        f"ConnectionStrings__FactoryState={connection};Username=factory_runtime;Password={runtime}\n"
        f"ConnectionStrings__FactoryMigrations={connection};Username=factory_migrator;Password={migrator}\n"
        "FACTORY_ADMIN_USER=admin\n"
        f"FACTORY_ADMIN_PASSWORD={app_admin}\n"
        f"FACTORY_DATA_DIR={data_dir}\n"
        "FACTORY_GITHUB_TOKEN=\n"
    )

print(f"Wrote .env.factory with generated passwords (not shown). Data directory: {data_dir}")
