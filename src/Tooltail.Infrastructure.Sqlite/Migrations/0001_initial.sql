CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY CHECK (version > 0),
    applied_utc TEXT NOT NULL CHECK (length(applied_utc) = 28 AND substr(applied_utc, 28, 1) = 'Z'),
    application_version TEXT NOT NULL CHECK (length(application_version) BETWEEN 1 AND 64),
    checksum TEXT NOT NULL CHECK (
        length(checksum) = 64 AND
        checksum NOT GLOB '*[^0-9a-f]*'
    )
) STRICT;

CREATE TABLE companions (
    companion_id TEXT PRIMARY KEY CHECK (length(companion_id) = 36),
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 200),
    created_utc TEXT NOT NULL CHECK (length(created_utc) = 28 AND substr(created_utc, 28, 1) = 'Z'),
    identity_schema_version INTEGER NOT NULL CHECK (identity_schema_version > 0),
    presentation_json TEXT NOT NULL CHECK (json_valid(presentation_json))
) STRICT;

CREATE TABLE window_leases (
    lease_id TEXT PRIMARY KEY CHECK (length(lease_id) = 36),
    companion_id TEXT NOT NULL REFERENCES companions(companion_id) ON DELETE RESTRICT,
    hwnd_value INTEGER NOT NULL CHECK (hwnd_value > 0),
    process_id INTEGER NOT NULL CHECK (process_id > 0),
    process_start_identity TEXT NOT NULL CHECK (length(process_start_identity) BETWEEN 1 AND 256),
    root_hwnd_value INTEGER NOT NULL CHECK (root_hwnd_value > 0),
    application_name TEXT NOT NULL CHECK (length(application_name) BETWEEN 1 AND 200),
    issued_utc TEXT NOT NULL CHECK (length(issued_utc) = 28 AND substr(issued_utc, 28, 1) = 'Z'),
    expires_utc TEXT NOT NULL CHECK (length(expires_utc) = 28 AND substr(expires_utc, 28, 1) = 'Z'),
    revoked_utc TEXT NULL CHECK (revoked_utc IS NULL OR (length(revoked_utc) = 28 AND substr(revoked_utc, 28, 1) = 'Z')),
    revocation_reason TEXT NULL CHECK (revocation_reason IS NULL OR length(revocation_reason) BETWEEN 1 AND 128)
) STRICT;

CREATE TABLE resource_grants (
    grant_id TEXT PRIMARY KEY CHECK (length(grant_id) = 36),
    companion_id TEXT NOT NULL REFERENCES companions(companion_id) ON DELETE RESTRICT,
    resource_type TEXT NOT NULL CHECK (resource_type = 'local_folder'),
    root_identity TEXT NOT NULL CHECK (length(root_identity) BETWEEN 1 AND 256),
    canonical_root_protected BLOB NULL,
    capabilities_json TEXT NOT NULL CHECK (json_valid(capabilities_json) AND json_type(capabilities_json) = 'array'),
    issued_utc TEXT NOT NULL CHECK (length(issued_utc) = 28 AND substr(issued_utc, 28, 1) = 'Z'),
    expires_utc TEXT NULL CHECK (expires_utc IS NULL OR (length(expires_utc) = 28 AND substr(expires_utc, 28, 1) = 'Z')),
    revoked_utc TEXT NULL CHECK (revoked_utc IS NULL OR (length(revoked_utc) = 28 AND substr(revoked_utc, 28, 1) = 'Z')),
    revocation_reason TEXT NULL CHECK (revocation_reason IS NULL OR length(revocation_reason) BETWEEN 1 AND 128),
    grant_fingerprint TEXT NOT NULL CHECK (
        length(grant_fingerprint) = 64 AND
        grant_fingerprint NOT GLOB '*[^0-9a-f]*'
    )
) STRICT;

CREATE TABLE folder_snapshots (
    snapshot_id TEXT PRIMARY KEY CHECK (length(snapshot_id) = 36),
    grant_id TEXT NOT NULL REFERENCES resource_grants(grant_id) ON DELETE RESTRICT,
    root_identity TEXT NOT NULL CHECK (length(root_identity) BETWEEN 1 AND 256),
    started_utc TEXT NOT NULL CHECK (length(started_utc) = 28 AND substr(started_utc, 28, 1) = 'Z'),
    completed_utc TEXT NOT NULL CHECK (length(completed_utc) = 28 AND substr(completed_utc, 28, 1) = 'Z'),
    status TEXT NOT NULL CHECK (status IN ('complete', 'incomplete', 'invalid')),
    reason_code TEXT NULL CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 128),
    hashed_bytes INTEGER NOT NULL CHECK (hashed_bytes >= 0),
    snapshot_json TEXT NOT NULL CHECK (json_valid(snapshot_json)),
    expires_utc TEXT NULL CHECK (expires_utc IS NULL OR (length(expires_utc) = 28 AND substr(expires_utc, 28, 1) = 'Z'))
) STRICT;

CREATE TABLE teaching_episodes (
    episode_id TEXT PRIMARY KEY CHECK (length(episode_id) = 36),
    companion_id TEXT NOT NULL REFERENCES companions(companion_id) ON DELETE RESTRICT,
    grant_id TEXT NOT NULL REFERENCES resource_grants(grant_id) ON DELETE RESTRICT,
    started_utc TEXT NOT NULL CHECK (length(started_utc) = 28 AND substr(started_utc, 28, 1) = 'Z'),
    stopped_utc TEXT NULL CHECK (stopped_utc IS NULL OR (length(stopped_utc) = 28 AND substr(stopped_utc, 28, 1) = 'Z')),
    status TEXT NOT NULL CHECK (status IN ('started', 'baseline_captured', 'observing_effects', 'stopped', 'reconciled', 'invalid')),
    evidence_status TEXT NOT NULL CHECK (evidence_status IN ('pending', 'complete', 'incomplete', 'ambiguous', 'unsupported')),
    baseline_snapshot_ref TEXT NULL REFERENCES folder_snapshots(snapshot_id) ON DELETE RESTRICT,
    final_snapshot_ref TEXT NULL REFERENCES folder_snapshots(snapshot_id) ON DELETE RESTRICT,
    reconciliation_summary_json TEXT NULL CHECK (reconciliation_summary_json IS NULL OR json_valid(reconciliation_summary_json)),
    invalid_reason TEXT NULL CHECK (invalid_reason IS NULL OR length(invalid_reason) BETWEEN 1 AND 128),
    raw_evidence_expiry_utc TEXT NULL CHECK (raw_evidence_expiry_utc IS NULL OR (length(raw_evidence_expiry_utc) = 28 AND substr(raw_evidence_expiry_utc, 28, 1) = 'Z'))
) STRICT;

CREATE TABLE demonstration_examples (
    example_id TEXT PRIMARY KEY CHECK (length(example_id) = 36),
    episode_id TEXT NOT NULL REFERENCES teaching_episodes(episode_id) ON DELETE RESTRICT,
    effect_type TEXT NOT NULL CHECK (effect_type IN ('ensure_directory', 'rename_file', 'move_file', 'copy_file')),
    source_relative_path TEXT NULL CHECK (source_relative_path IS NULL OR length(source_relative_path) BETWEEN 1 AND 1024),
    destination_relative_path TEXT NOT NULL CHECK (length(destination_relative_path) BETWEEN 1 AND 1024),
    source_fingerprint_json TEXT NULL CHECK (source_fingerprint_json IS NULL OR json_valid(source_fingerprint_json)),
    user_label TEXT NULL CHECK (user_label IS NULL OR length(user_label) <= 200)
) STRICT;

CREATE TABLE skills (
    skill_id TEXT PRIMARY KEY CHECK (length(skill_id) = 36),
    companion_id TEXT NOT NULL REFERENCES companions(companion_id) ON DELETE RESTRICT,
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 200),
    created_utc TEXT NOT NULL CHECK (length(created_utc) = 28 AND substr(created_utc, 28, 1) = 'Z'),
    current_version_id TEXT NULL REFERENCES skill_versions(skill_version_id) ON DELETE RESTRICT,
    disabled_utc TEXT NULL CHECK (disabled_utc IS NULL OR (length(disabled_utc) = 28 AND substr(disabled_utc, 28, 1) = 'Z'))
) STRICT;

CREATE TABLE skill_versions (
    skill_version_id TEXT PRIMARY KEY CHECK (length(skill_version_id) BETWEEN 1 AND 80),
    skill_id TEXT NOT NULL REFERENCES skills(skill_id) ON DELETE RESTRICT,
    version_number INTEGER NOT NULL CHECK (version_number > 0),
    parent_version_id TEXT NULL REFERENCES skill_versions(skill_version_id) ON DELETE RESTRICT,
    schema_version TEXT NOT NULL CHECK (length(schema_version) BETWEEN 1 AND 64),
    skill_spec_json TEXT NOT NULL CHECK (json_valid(skill_spec_json)),
    spec_hash TEXT NOT NULL CHECK (length(spec_hash) = 64 AND spec_hash NOT GLOB '*[^0-9a-f]*'),
    compiler_id TEXT NOT NULL CHECK (length(compiler_id) BETWEEN 1 AND 128),
    compiler_version TEXT NOT NULL CHECK (length(compiler_version) BETWEEN 1 AND 64),
    executor_compatibility TEXT NOT NULL CHECK (length(executor_compatibility) BETWEEN 1 AND 64),
    lifecycle_state TEXT NOT NULL CHECK (lifecycle_state IN ('draft', 'approved', 'practiced', 'reliable', 'delegated', 'stale')),
    created_utc TEXT NOT NULL CHECK (length(created_utc) = 28 AND substr(created_utc, 28, 1) = 'Z'),
    approved_utc TEXT NULL CHECK (approved_utc IS NULL OR (length(approved_utc) = 28 AND substr(approved_utc, 28, 1) = 'Z')),
    semantic_diff_json TEXT NULL CHECK (semantic_diff_json IS NULL OR json_valid(semantic_diff_json)),
    UNIQUE (skill_id, version_number)
) STRICT;

CREATE UNIQUE INDEX ux_skill_versions_skill_hash
    ON skill_versions(skill_id, spec_hash);

CREATE TABLE skill_evidence (
    evidence_id TEXT PRIMARY KEY CHECK (length(evidence_id) = 36),
    skill_version_id TEXT NOT NULL REFERENCES skill_versions(skill_version_id) ON DELETE RESTRICT,
    evidence_type TEXT NOT NULL CHECK (length(evidence_type) BETWEEN 1 AND 64),
    source_id TEXT NOT NULL CHECK (length(source_id) BETWEEN 1 AND 128),
    summary_json TEXT NOT NULL CHECK (json_valid(summary_json)),
    created_utc TEXT NOT NULL CHECK (length(created_utc) = 28 AND substr(created_utc, 28, 1) = 'Z')
) STRICT;

CREATE TABLE execution_plans (
    plan_id TEXT PRIMARY KEY CHECK (length(plan_id) = 36),
    plan_kind TEXT NOT NULL CHECK (plan_kind IN ('standard', 'recovery')),
    skill_version_id TEXT NOT NULL REFERENCES skill_versions(skill_version_id) ON DELETE RESTRICT,
    grant_id TEXT NOT NULL REFERENCES resource_grants(grant_id) ON DELETE RESTRICT,
    original_execution_id TEXT NULL REFERENCES executions(execution_id) ON DELETE RESTRICT CHECK (original_execution_id IS NULL OR length(original_execution_id) = 36),
    original_plan_id TEXT NULL REFERENCES execution_plans(plan_id) ON DELETE RESTRICT,
    original_plan_fingerprint TEXT NULL CHECK (original_plan_fingerprint IS NULL OR (length(original_plan_fingerprint) = 64 AND original_plan_fingerprint NOT GLOB '*[^0-9a-f]*')),
    plan_contract_version TEXT NOT NULL CHECK (length(plan_contract_version) BETWEEN 1 AND 64),
    created_utc TEXT NOT NULL CHECK (length(created_utc) = 28 AND substr(created_utc, 28, 1) = 'Z'),
    plan_json TEXT NOT NULL CHECK (json_valid(plan_json)),
    plan_fingerprint TEXT NOT NULL CHECK (length(plan_fingerprint) = 64 AND plan_fingerprint NOT GLOB '*[^0-9a-f]*'),
    status TEXT NOT NULL CHECK (status IN ('planned', 'approved', 'consumed', 'expired', 'invalid')),
    expires_utc TEXT NOT NULL CHECK (length(expires_utc) = 28 AND substr(expires_utc, 28, 1) = 'Z'),
    CHECK (
        (plan_kind = 'standard' AND original_execution_id IS NULL AND original_plan_id IS NULL AND original_plan_fingerprint IS NULL) OR
        (plan_kind = 'recovery' AND original_execution_id IS NOT NULL AND original_plan_id IS NOT NULL AND original_plan_fingerprint IS NOT NULL)
    )
) STRICT;

CREATE UNIQUE INDEX ux_execution_plans_fingerprint
    ON execution_plans(plan_id, plan_fingerprint);

CREATE TABLE approvals (
    approval_id TEXT PRIMARY KEY CHECK (length(approval_id) = 36),
    plan_id TEXT NOT NULL REFERENCES execution_plans(plan_id) ON DELETE RESTRICT,
    plan_fingerprint TEXT NOT NULL CHECK (length(plan_fingerprint) = 64 AND plan_fingerprint NOT GLOB '*[^0-9a-f]*'),
    approved_utc TEXT NOT NULL CHECK (length(approved_utc) = 28 AND substr(approved_utc, 28, 1) = 'Z'),
    expires_utc TEXT NOT NULL CHECK (length(expires_utc) = 28 AND substr(expires_utc, 28, 1) = 'Z'),
    approval_purpose TEXT NOT NULL CHECK (approval_purpose IN ('production', 'rehearsal', 'undo')),
    consumed_utc TEXT NULL CHECK (consumed_utc IS NULL OR (length(consumed_utc) = 28 AND substr(consumed_utc, 28, 1) = 'Z')),
    revoked_utc TEXT NULL CHECK (revoked_utc IS NULL OR (length(revoked_utc) = 28 AND substr(revoked_utc, 28, 1) = 'Z')),
    revocation_reason TEXT NULL CHECK (revocation_reason IS NULL OR length(revocation_reason) BETWEEN 1 AND 128),
    CHECK (NOT (consumed_utc IS NOT NULL AND revoked_utc IS NOT NULL)),
    FOREIGN KEY (plan_id, plan_fingerprint)
        REFERENCES execution_plans(plan_id, plan_fingerprint) ON DELETE RESTRICT
) STRICT;

CREATE TABLE executions (
    execution_id TEXT PRIMARY KEY CHECK (length(execution_id) = 36),
    plan_id TEXT NOT NULL REFERENCES execution_plans(plan_id) ON DELETE RESTRICT,
    approval_id TEXT NOT NULL UNIQUE REFERENCES approvals(approval_id) ON DELETE RESTRICT,
    correlation_id TEXT NULL CHECK (correlation_id IS NULL OR length(correlation_id) BETWEEN 1 AND 128),
    journal_kind TEXT NOT NULL CHECK (journal_kind IN ('standard', 'recovery')),
    operation_primitives_json TEXT NOT NULL CHECK (json_valid(operation_primitives_json) AND json_type(operation_primitives_json) = 'array'),
    operation_inverse_kinds_json TEXT NOT NULL CHECK (json_valid(operation_inverse_kinds_json) AND json_type(operation_inverse_kinds_json) = 'array'),
    recovery_primitives_json TEXT NOT NULL CHECK (json_valid(recovery_primitives_json) AND json_type(recovery_primitives_json) = 'array'),
    recovery_original_steps_json TEXT NOT NULL CHECK (json_valid(recovery_original_steps_json) AND json_type(recovery_original_steps_json) = 'array'),
    started_utc TEXT NOT NULL CHECK (length(started_utc) = 28 AND substr(started_utc, 28, 1) = 'Z'),
    completed_utc TEXT NULL CHECK (completed_utc IS NULL OR (length(completed_utc) = 28 AND substr(completed_utc, 28, 1) = 'Z')),
    status TEXT NOT NULL CHECK (status IN ('running', 'verified', 'failed', 'recovery_required', 'cancelled')),
    verification_summary_json TEXT NULL CHECK (verification_summary_json IS NULL OR json_valid(verification_summary_json)),
    residual_effects_json TEXT NULL CHECK (residual_effects_json IS NULL OR (json_valid(residual_effects_json) AND json_type(residual_effects_json) = 'array'))
) STRICT;

CREATE TABLE execution_journal_events (
    journal_event_id TEXT PRIMARY KEY CHECK (length(journal_event_id) BETWEEN 1 AND 80),
    execution_id TEXT NOT NULL REFERENCES executions(execution_id) ON DELETE RESTRICT,
    event_sequence INTEGER NOT NULL CHECK (event_sequence > 0),
    step_sequence INTEGER NULL CHECK (step_sequence IS NULL OR step_sequence > 0),
    event_type TEXT NOT NULL CHECK (event_type IN (
        'execution_opened',
        'step_intent',
        'recovery_step_intent',
        'mutation_observed',
        'step_committed',
        'step_verified',
        'step_failed',
        'recovery_required',
        'step_rolled_back'
    )),
    event_version INTEGER NOT NULL CHECK (event_version = 1),
    occurred_utc TEXT NOT NULL CHECK (length(occurred_utc) = 28 AND substr(occurred_utc, 28, 1) = 'Z'),
    primitive_type TEXT NULL CHECK (primitive_type IS NULL OR primitive_type IN ('ensure_directory', 'rename_file', 'move_file', 'copy_file')),
    recovery_primitive_type TEXT NULL CHECK (recovery_primitive_type IS NULL OR recovery_primitive_type IN ('rename_back', 'move_back', 'remove_created_entry')),
    original_step_sequence INTEGER NULL CHECK (original_step_sequence IS NULL OR original_step_sequence > 0),
    precondition_fingerprint TEXT NULL CHECK (precondition_fingerprint IS NULL OR (length(precondition_fingerprint) = 64 AND precondition_fingerprint NOT GLOB '*[^0-9a-f]*')),
    inverse_kind TEXT NULL CHECK (inverse_kind IS NULL OR inverse_kind IN ('none', 'rename_back', 'move_back', 'remove_created_entry')),
    reason_code TEXT NULL CHECK (reason_code IS NULL OR (length(reason_code) BETWEEN 1 AND 128 AND reason_code NOT GLOB '*[^a-z0-9._-]*')),
    recovery_execution_id TEXT NULL REFERENCES executions(execution_id) ON DELETE RESTRICT CHECK (recovery_execution_id IS NULL OR length(recovery_execution_id) = 36),
    UNIQUE (execution_id, event_sequence),
    CHECK (
        (event_type = 'execution_opened' AND step_sequence IS NULL AND primitive_type IS NULL AND recovery_primitive_type IS NULL AND original_step_sequence IS NULL AND precondition_fingerprint IS NOT NULL AND inverse_kind IS NULL AND reason_code IS NULL AND recovery_execution_id IS NULL) OR
        (event_type = 'step_intent' AND step_sequence IS NOT NULL AND primitive_type IS NOT NULL AND recovery_primitive_type IS NULL AND original_step_sequence IS NULL AND precondition_fingerprint IS NOT NULL AND inverse_kind IS NOT NULL AND reason_code IS NULL AND recovery_execution_id IS NULL) OR
        (event_type = 'recovery_step_intent' AND step_sequence IS NOT NULL AND primitive_type IS NULL AND recovery_primitive_type IS NOT NULL AND original_step_sequence IS NOT NULL AND precondition_fingerprint IS NOT NULL AND inverse_kind IS NULL AND reason_code IS NULL AND recovery_execution_id IS NULL) OR
        (event_type IN ('mutation_observed', 'step_committed', 'step_verified') AND step_sequence IS NOT NULL AND primitive_type IS NULL AND recovery_primitive_type IS NULL AND original_step_sequence IS NULL AND precondition_fingerprint IS NULL AND inverse_kind IS NULL AND reason_code IS NULL AND recovery_execution_id IS NULL) OR
        (event_type IN ('step_failed', 'recovery_required') AND step_sequence IS NOT NULL AND primitive_type IS NULL AND recovery_primitive_type IS NULL AND original_step_sequence IS NULL AND precondition_fingerprint IS NULL AND inverse_kind IS NULL AND reason_code IS NOT NULL AND recovery_execution_id IS NULL) OR
        (event_type = 'step_rolled_back' AND step_sequence IS NOT NULL AND primitive_type IS NULL AND recovery_primitive_type IS NULL AND original_step_sequence IS NULL AND precondition_fingerprint IS NULL AND inverse_kind IS NULL AND reason_code IS NULL AND recovery_execution_id IS NOT NULL)
    )
) STRICT;

CREATE INDEX ix_execution_journal_events_recovery
    ON execution_journal_events(event_type, recovery_execution_id)
    WHERE recovery_execution_id IS NOT NULL;

CREATE TABLE receipts (
    receipt_id TEXT PRIMARY KEY CHECK (length(receipt_id) = 36),
    execution_id TEXT NOT NULL UNIQUE REFERENCES executions(execution_id) ON DELETE RESTRICT,
    receipt_kind TEXT NOT NULL CHECK (receipt_kind IN ('standard', 'recovery')),
    receipt_json TEXT NOT NULL CHECK (json_valid(receipt_json)),
    created_utc TEXT NOT NULL CHECK (length(created_utc) = 28 AND substr(created_utc, 28, 1) = 'Z'),
    undo_available_until_utc TEXT NULL CHECK (undo_available_until_utc IS NULL OR (length(undo_available_until_utc) = 28 AND substr(undo_available_until_utc, 28, 1) = 'Z'))
) STRICT;

CREATE TABLE agent_runs (
    agent_run_id TEXT PRIMARY KEY CHECK (length(agent_run_id) = 36),
    adapter_id TEXT NOT NULL CHECK (length(adapter_id) BETWEEN 1 AND 128),
    external_run_id_hash TEXT NOT NULL CHECK (length(external_run_id_hash) = 64 AND external_run_id_hash NOT GLOB '*[^0-9a-f]*'),
    started_utc TEXT NOT NULL CHECK (length(started_utc) = 28 AND substr(started_utc, 28, 1) = 'Z'),
    completed_utc TEXT NULL CHECK (completed_utc IS NULL OR (length(completed_utc) = 28 AND substr(completed_utc, 28, 1) = 'Z')),
    normalized_status TEXT NOT NULL CHECK (normalized_status IN ('pending', 'active', 'blocked', 'completed', 'failed', 'cancelled')),
    unknown_event_count INTEGER NOT NULL CHECK (unknown_event_count >= 0),
    failure_code TEXT NULL CHECK (failure_code IS NULL OR (length(failure_code) BETWEEN 1 AND 128 AND failure_code NOT GLOB '*[^a-z0-9._-]*'))
) STRICT;

CREATE TABLE domain_events (
    event_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    aggregate_type TEXT NOT NULL CHECK (length(aggregate_type) BETWEEN 1 AND 128),
    aggregate_id TEXT NOT NULL CHECK (length(aggregate_id) BETWEEN 1 AND 128),
    event_type TEXT NOT NULL CHECK (length(event_type) BETWEEN 1 AND 128),
    event_version INTEGER NOT NULL CHECK (event_version > 0),
    occurred_utc TEXT NOT NULL CHECK (length(occurred_utc) = 28 AND substr(occurred_utc, 28, 1) = 'Z'),
    correlation_id TEXT NULL CHECK (correlation_id IS NULL OR length(correlation_id) BETWEEN 1 AND 128),
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json))
) STRICT;

CREATE INDEX ix_domain_events_aggregate
    ON domain_events(aggregate_type, aggregate_id, event_sequence);

CREATE TRIGGER schema_migrations_no_update
BEFORE UPDATE ON schema_migrations
BEGIN
    SELECT RAISE(ABORT, 'schema_migrations_are_append_only');
END;

CREATE TRIGGER schema_migrations_no_delete
BEFORE DELETE ON schema_migrations
BEGIN
    SELECT RAISE(ABORT, 'schema_migrations_are_append_only');
END;

CREATE TRIGGER execution_journal_events_no_update
BEFORE UPDATE ON execution_journal_events
BEGIN
    SELECT RAISE(ABORT, 'execution_journal_is_append_only');
END;

CREATE TRIGGER execution_journal_events_no_delete
BEFORE DELETE ON execution_journal_events
BEGIN
    SELECT RAISE(ABORT, 'execution_journal_is_append_only');
END;

CREATE TRIGGER receipts_no_update
BEFORE UPDATE ON receipts
BEGIN
    SELECT RAISE(ABORT, 'receipts_are_immutable');
END;

CREATE TRIGGER receipts_no_delete
BEFORE DELETE ON receipts
BEGIN
    SELECT RAISE(ABORT, 'receipts_are_immutable');
END;

CREATE TRIGGER domain_events_no_update
BEFORE UPDATE ON domain_events
BEGIN
    SELECT RAISE(ABORT, 'domain_events_are_append_only');
END;

CREATE TRIGGER domain_events_no_delete
BEFORE DELETE ON domain_events
BEGIN
    SELECT RAISE(ABORT, 'domain_events_are_append_only');
END;

CREATE TRIGGER skill_versions_immutable_core
BEFORE UPDATE ON skill_versions
WHEN
    NEW.skill_version_id != OLD.skill_version_id OR
    NEW.skill_id != OLD.skill_id OR
    NEW.version_number != OLD.version_number OR
    NEW.parent_version_id IS NOT OLD.parent_version_id OR
    NEW.schema_version != OLD.schema_version OR
    NEW.skill_spec_json != OLD.skill_spec_json OR
    NEW.spec_hash != OLD.spec_hash OR
    NEW.compiler_id != OLD.compiler_id OR
    NEW.compiler_version != OLD.compiler_version OR
    NEW.executor_compatibility != OLD.executor_compatibility OR
    NEW.created_utc != OLD.created_utc
BEGIN
    SELECT RAISE(ABORT, 'skill_version_core_is_immutable');
END;
