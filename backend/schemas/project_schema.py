from pydantic import BaseModel
from typing import Optional, Dict, Any

class ProjectCreate(BaseModel):
    name: str
    description: Optional[str] = None
    settings: Optional[Dict[str, Any]] = None
