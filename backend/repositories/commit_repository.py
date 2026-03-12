from datetime import datetime
import uuid
from typing import List, Optional, Any
from sqlmodel import Session, select
from fastapi.encoders import jsonable_encoder
from database import engine
from entities.commit_entity import Commit
from entities.project_entity import Project
from schemas.element_schema import ElementSnapshot
from schemas.diff_schema import Change

_RESNAPSHOT_INTERVAL = 20

# Maximum commits to walk back when looking for a linear chain.
# Prevents runaway walks on very long histories.
_MAX_CHAIN_WALK = 200


class CommitRepository:

    # ------------------------------------------------------------------
    # Write
    # ------------------------------------------------------------------

    def create_commit(
        self,
        project_id: str,
        model_id: str,
        message: str,
        author: str,
        storage_url: Optional[str] = None,
        change_type: str = "MOD",
        parent_commit: Optional[str] = None,
        snapshot: Optional[ElementSnapshot] = None,
        diff: Optional[List[Change]] = None,
        element_count: Optional[int] = None,
        changed_elements: Optional[int] = None,
    ) -> Commit:
        """
        Create a commit storing either a full snapshot (root / checkpoint)
        or a delta (all other commits).

          parent_commit is None         :root, snapshot required
          parent_commit set, diff only  :normal delta commit
          parent_commit set, both given :forced re-snapshot checkpoint
        """
        with Session(engine) as session:
            commit_id = str(uuid.uuid4())

            is_full = parent_commit is None
            if snapshot is not None and diff is not None:
                is_full = True  # explicit re-snapshot checkpoint

            if is_full:
                if snapshot is None:
                    raise ValueError("Root commit requires a full snapshot.")
                payload = jsonable_encoder(snapshot)
            else:
                if diff is None:
                    raise ValueError("Delta commit requires a diff.")
                payload = jsonable_encoder(diff)

            commit = Commit(
                commitId=commit_id,
                projectId=project_id,
                modelId=model_id,
                message=message,
                author=author,
                timestamp=datetime.utcnow(),
                parentCommit=parent_commit,
                storageUrl=storage_url,
                changeType=change_type,
                isFullSnapshot=is_full,
                elementCount=element_count if element_count is not None else (
                    len(snapshot.elements) if snapshot else 0
                ),
                changedElements=changed_elements if changed_elements is not None else (
                    len(diff) if diff else 0
                ),
                snapshot=payload,
            )

            project = session.get(Project, project_id)
            if project:
                project.lastModified = datetime.utcnow()
                session.add(project)

            session.add(commit)
            session.commit()
            session.refresh(commit)
            return commit

    # ------------------------------------------------------------------
    # Simple reads
    # ------------------------------------------------------------------

    def get_commit(self, commit_id: str) -> Optional[Commit]:
        with Session(engine) as session:
            return session.get(Commit, commit_id)

    def get_latest_commit(self, project_id: str) -> Optional[Commit]:
        with Session(engine) as session:
            statement = (
                select(Commit)
                .where(Commit.projectId == project_id)
                .order_by(Commit.timestamp.desc())
                .limit(1)
            )
            return session.exec(statement).first()

    def get_latest_commit_for_model(
        self, project_id: str, model_id: Optional[str]
    ) -> Optional[Commit]:
        if not model_id:
            return self.get_latest_commit(project_id)

        with Session(engine) as session:
            statement = (
                select(Commit)
                .where(Commit.projectId == project_id)
                .where(Commit.modelId == model_id)
                .order_by(Commit.timestamp.desc())
                .limit(1)
            )
            return session.exec(statement).first()

    def list_commits(
        self, project_id: str, limit: int = 50, offset: int = 0
    ) -> List[Commit]:
        with Session(engine) as session:
            statement = (
                select(Commit)
                .where(Commit.projectId == project_id)
                .order_by(Commit.timestamp.desc())
                .offset(offset)
                .limit(limit)
            )
            return session.exec(statement).all()

    def get_commit_count(self, project_id: str) -> int:
        with Session(engine) as session:
            statement = select(Commit).where(Commit.projectId == project_id)
            return len(session.exec(statement).all())

    # ------------------------------------------------------------------
    # Fast path — linear chain delta collection
    # ------------------------------------------------------------------

    def get_linear_chain_deltas(
        self,
        current_commit_id: str,
        target_commit_id: str,
    ) -> Optional[List[Change]]:
        """
        Walk backwards from target_commit_id through parentCommit links.
        If current_commit_id is found in the chain without crossing a
        full-snapshot boundary, collect every stored delta along the way
        and return them in forward order (oldest to newest).

        Returns None when:
          - the chain exceeds _MAX_CHAIN_WALK steps
          - a full-snapshot commit is encountered before reaching current
          - current_commit_id is not an ancestor of target_commit_id

        The caller should fall back to full reconstruction on None.
        """
        # Walk backwards, collecting commits in reverse order
        chain: List[Commit] = []
        current_id: Optional[str] = target_commit_id
        steps = 0

        while current_id is not None and steps < _MAX_CHAIN_WALK:
            with Session(engine) as session:
                commit = session.get(Commit, current_id)

            if commit is None:
                # Broken chain
                return None

            # Reached the user's current commit — chain is complete
            if current_id == current_commit_id:
                break

            # Hit a full snapshot before reaching current_commit_id.
            # Cannot use the fast path — the delta chain crossed a boundary.
            if commit.isFullSnapshot and current_id != target_commit_id:
                return None

            chain.append(commit)
            current_id = commit.parentCommit
            steps += 1
        else:
            # Loop exhausted _MAX_CHAIN_WALK without finding current_commit_id
            return None

        if not chain:
            # target IS current — no changes
            return []

        # chain is in reverse order (target : current+1), flip it
        chain.reverse()

        # Extract and flatten all stored deltas in forward order
        all_changes: List[Change] = []
        for commit in chain:
            if commit.snapshot is None:
                return None  # Missing delta — cannot use fast path
            all_changes.extend([Change(**c) for c in commit.snapshot])

        return all_changes

    def get_stored_delta(self, commit_id: str) -> Optional[List[Change]]:
        """
        Return the raw stored delta for a single delta commit.
        Returns None if the commit is a full snapshot or does not exist.
        """
        with Session(engine) as session:
            commit = session.get(Commit, commit_id)

        if commit is None or commit.snapshot is None or commit.isFullSnapshot:
            return None

        return [Change(**c) for c in commit.snapshot]

    def is_direct_child(
        self, parent_commit_id: str, child_commit_id: str
    ) -> bool:
        with Session(engine) as session:
            child = session.get(Commit, child_commit_id)
        return child is not None and child.parentCommit == parent_commit_id

    # ------------------------------------------------------------------
    # Slow path — full recursive reconstruction
    # ------------------------------------------------------------------

    def get_snapshot(self, commit_id: str) -> Optional[ElementSnapshot]:
        """
        Reconstruct the full ElementSnapshot for any commit.
        Full snapshot  : deserialise and return directly.
        Delta commit   : recurse to parent and replay stored changes.
        Depth is bounded by _RESNAPSHOT_INTERVAL.
        """
        from diff_engine import DiffEngine

        with Session(engine) as session:
            commit = session.get(Commit, commit_id)

        if commit is None or commit.snapshot is None:
            return None

        # Base case
        if commit.isFullSnapshot:
            return ElementSnapshot(**commit.snapshot)

        if commit.parentCommit is None:
            return None

        parent_snapshot = self.get_snapshot(commit.parentCommit)
        if parent_snapshot is None:
            return None

        changes = [Change(**c) for c in commit.snapshot]
        engine_instance = DiffEngine()
        updated_elements = engine_instance.apply_changes(
            base_elements=parent_snapshot.elements,
            changes=changes,
        )

        return ElementSnapshot(
            version=parent_snapshot.version,
            projectId=parent_snapshot.projectId,
            modelId=parent_snapshot.modelId,
            timestamp=parent_snapshot.timestamp,
            userName=parent_snapshot.userName,
            commitMessage=parent_snapshot.commitMessage,
            elements=updated_elements,
        )

    def count_delta_depth(self, commit_id: str) -> int:
        depth = 0
        current_id = commit_id
        while current_id:
            with Session(engine) as session:
                commit = session.get(Commit, current_id)
            if commit is None or commit.isFullSnapshot:
                break
            depth += 1
            current_id = commit.parentCommit
        return depth