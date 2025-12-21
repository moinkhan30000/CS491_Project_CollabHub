from typing import List, Optional
from datetime import datetime
from sqlmodel import Session, select
from database import engine
from entities.project_member_entity import ProjectMember
from entities.project_entity import Project

class ProjectMemberRepository:
    def add_member(self, project_id: str, user_id: str, role: str, status: str = "PENDING") -> ProjectMember:
        with Session(engine) as session:
            member = ProjectMember(
                projectId=project_id,
                userId=user_id,
                role=role,
                status=status,
                invitedAt=datetime.utcnow()
            )
            session.add(member)
            session.commit()
            session.refresh(member)
            return member

    def get_member(self, project_id: str, user_id: str) -> Optional[ProjectMember]:
        with Session(engine) as session:
            statement = select(ProjectMember).where(
                ProjectMember.projectId == project_id,
                ProjectMember.userId == user_id
            )
            return session.exec(statement).first()

    def get_pending_invites(self, user_id: str) -> List[dict]:
        """Returns list of invites with Project details"""
        from entities.commit_entity import Commit
        from pathlib import Path
        
        with Session(engine) as session:
            # Join ProjectMember with Project to get details
            statement = select(ProjectMember, Project).where(
                ProjectMember.userId == user_id,
                ProjectMember.status == "PENDING",
                ProjectMember.projectId == Project.projectId
            )
            results = session.exec(statement).all()
            
            invites = []
            for member, project in results:
                # Get file extension from latest commit
                latest_commit = session.exec(
                    select(Commit)
                    .where(Commit.projectId == project.projectId)
                    .order_by(Commit.timestamp.desc())
                ).first()
                
                file_ext = ".rvt"  # default
                if latest_commit and latest_commit.storageUrl:
                    file_ext = Path(latest_commit.storageUrl).suffix or ".rvt"
                
                invites.append({
                    "inviteId": member.id,
                    "projectId": project.projectId,
                    "projectName": project.name,
                    "invitedAt": member.invitedAt,
                    "role": member.role,
                    "fileExtension": file_ext
                })
            return invites

    def update_status(self, member_id: int, status: str) -> Optional[ProjectMember]:
        with Session(engine) as session:
            member = session.get(ProjectMember, member_id)
            if member:
                member.status = status
                session.add(member)
                session.commit()
                session.refresh(member)
            return member

    def get_user_projects(self, user_id: str) -> List[dict]:
        """Returns list of projects where user is an ACTIVE member"""
        with Session(engine) as session:
            statement = select(ProjectMember, Project).where(
                ProjectMember.userId == user_id,
                ProjectMember.status == "ACTIVE",
                ProjectMember.projectId == Project.projectId
            )
            results = session.exec(statement).all()
            
            projects = []
            for member, project in results:
                projects.append({
                    "projectId": project.projectId,
                    "name": project.name,
                    "description": project.description,
                    "role": member.role,
                    "createdAt": project.createdAt,
                    "lastModified": project.lastModified
                })
            return projects
