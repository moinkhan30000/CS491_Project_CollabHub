"""
FastAPI Main Application
Revit Version Control Backend Server
"""

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from datetime import datetime
from database import create_db_and_tables, seed_dev_test_users
import uvicorn

# Import routers (will create these)
from routers import auth, projects, snapshots, diff, merge, base_files

app = FastAPI(
    title="Revit Version Control API",
    description="Backend API for Revit element-level version control",
    version="1.0.0",
    docs_url="/api/docs",
    redoc_url="/api/redoc"
)

# CORS configuration
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # Configure appropriately for production
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

@app.on_event("startup")
def on_startup():
    create_db_and_tables()
    seed_dev_test_users()

# Include routers
app.include_router(auth.router, prefix="/api/v1/auth", tags=["Authentication"])
app.include_router(projects.router, prefix="/api/v1/projects", tags=["Projects"])
app.include_router(snapshots.router, prefix="/api/v1/projects", tags=["Snapshots"])
app.include_router(base_files.router, prefix="/api/v1/projects", tags=["BaseFiles"])
app.include_router(diff.router, prefix="/api/v1/projects", tags=["Diff"])
app.include_router(merge.router, prefix="/api/v1/projects", tags=["Merge"])

@app.get("/api/v1/health")
async def health_check():
    """Health check endpoint"""
    return {
        "status": "healthy",
        "version": "1.0.0",
        "timestamp": datetime.utcnow().isoformat() + "Z",
        "database": "connected",
        "storage": "available"
    }

@app.get("/")
async def root():
    """Root endpoint"""
    return {
        "message": "Revit Version Control API",
        "version": "1.0.0",
        "docs": "/api/docs"
    }

# Global exception handler
@app.exception_handler(Exception)
async def global_exception_handler(request, exc):
    return JSONResponse(
        status_code=500,
        content={
            "error": {
                "code": "INTERNAL_ERROR",
                "message": str(exc),
                "timestamp": datetime.utcnow().isoformat() + "Z"
            }
        }
    )

if __name__ == "__main__":
    uvicorn.run(
        "main:app",
        host="0.0.0.0",
        port=8000,
        reload=True,
        log_level="info"
    )
