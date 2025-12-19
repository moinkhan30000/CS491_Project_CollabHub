"""
DEPRECATED
This module is deprecated.
Database operations have been moved to:
- backend.repositories.user_repository
- backend.repositories.project_repository
- backend.repositories.commit_repository
"""

class Storage:
    def __init__(self):
        raise RuntimeError("Storage class is deprecated. Use Repositories instead.")

# storage = Storage() # Commented out to prevent usage