import sys
import os

# Add backend to path so we can import main
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '../backend')))

from fastapi.testclient import TestClient
from main import app
from entities.user_entity import User
from entities.token_entity import RefreshToken
from database import create_db_and_tables

# Setup DB (ensure tables exist)
# Note: This might touch the real DB if configured in .env. 
# For safety in a real env we might want a test DB, but for this task assume dev env.
create_db_and_tables()

client = TestClient(app)

def test_auth_flow():
    email = f"test_{os.urandom(4).hex()}@example.com"
    password = "password123"
    fullname = "Test User"

    print(f"1. Registering user: {email}")
    response = client.post("/api/v1/auth/register", json={
        "email": email,
        "password": password,
        "fullName": fullname
    })
    if response.status_code == 400:
        print("User already exists, proceeding to login.")
    elif response.status_code != 201:
        print(f"Registration failed: {response.status_code}")
        print(response.text)
        assert response.status_code == 201
    else:
        print("   Success. Checking for Auto-Login tokens...")
        reg_data = response.json()
        assert "accessToken" in reg_data
        assert "refreshToken" in reg_data
        print("   Auto-Login successful (Tokens received).")

    print("2. Logging in (Verifying manual login still works)")
    response = client.post("/api/v1/auth/login", json={
        "email": email,
        "password": password
    })
    assert response.status_code == 200
    tokens = response.json()
    access_token = tokens["accessToken"]
    refresh_token = tokens["refreshToken"]
    print("   Success. Got manual login tokens.")

    print("3. Refreshing token")
    response = client.post(f"/api/v1/auth/refresh?refresh_token={refresh_token}")
    assert response.status_code == 200
    new_tokens = response.json()
    new_access_token = new_tokens["accessToken"]
    new_refresh_token = new_tokens["refreshToken"]
    print("   Success. Got new tokens.")
    
    # Verify new access token is different (or at least valid)
    assert new_access_token != ""

    print("4. Logout")
    response = client.post(f"/api/v1/auth/logout?refresh_token={new_refresh_token}")
    assert response.status_code == 200
    print("   Success.")

    print("5. Verify Refresh Token is revoked")
    response = client.post(f"/api/v1/auth/refresh?refresh_token={new_refresh_token}")
    assert response.status_code == 401
    print("   Success. Token revoked.")

    print("\nALL TESTS PASSED")

if __name__ == "__main__":
    test_auth_flow()
