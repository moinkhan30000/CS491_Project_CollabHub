import os
import shutil
from fastapi import UploadFile
from pathlib import Path

# Data directory mapped to Docker volume
DATA_DIR = Path("/app/data")

class StorageService:
    def __init__(self):
        # Ensure base directory exists
        DATA_DIR.mkdir(parents=True, exist_ok=True)

    def _get_project_dir(self, project_id: str) -> Path:
        path = DATA_DIR / project_id
        path.mkdir(parents=True, exist_ok=True)
        return path

    async def upload_file(self, file: UploadFile, project_id: str, commit_id: str) -> str:
        """
        Saves an uploaded file to the object storage (Docker volume).
        Returns the relative storage path (storageUrl).
        """
        project_dir = self._get_project_dir(project_id)
        # Use commit_id as filename to ensure version uniqueness
        # Append original extension if possible, default to .rvt
        ext = Path(file.filename).suffix if file.filename else ".rvt"
        filename = f"{commit_id}{ext}"
        file_path = project_dir / filename

        with open(file_path, "wb") as buffer:
            shutil.copyfileobj(file.file, buffer)
        
        # Return path relative to DATA_DIR so it's portable
        return f"{project_id}/{filename}"

    def get_file_path(self, storage_url: str) -> Path:
        """
        Resolves a storageUrl to an absolute system path.
        """
        return DATA_DIR / storage_url
