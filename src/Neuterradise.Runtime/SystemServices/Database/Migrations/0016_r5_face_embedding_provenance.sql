-- R5 / X33: face_detections model_id/model_version remain YuNet detector provenance.
-- identity_samples model_id/model_version must instead match the SFace embedding-space key.
UPDATE identity_samples
SET model_id = substr(embedding_space_key, 1, instr(embedding_space_key, '|') - 1),
    model_version = substr(
        substr(embedding_space_key, instr(embedding_space_key, '|') + 1),
        1,
        instr(substr(embedding_space_key, instr(embedding_space_key, '|') + 1), '|') - 1)
WHERE instr(embedding_space_key, '|') > 1
  AND instr(substr(embedding_space_key, instr(embedding_space_key, '|') + 1), '|') > 1;

CREATE TRIGGER trg_identity_samples_embedding_provenance_insert
BEFORE INSERT ON identity_samples
WHEN NEW.embedding_space_key NOT LIKE NEW.model_id || '|' || NEW.model_version || '|%'
BEGIN
    SELECT RAISE(ABORT, 'identity sample model provenance must match embedding_space_key');
END;

CREATE TRIGGER trg_identity_samples_embedding_provenance_update
BEFORE UPDATE OF embedding_space_key, model_id, model_version ON identity_samples
WHEN NEW.embedding_space_key NOT LIKE NEW.model_id || '|' || NEW.model_version || '|%'
BEGIN
    SELECT RAISE(ABORT, 'identity sample model provenance must match embedding_space_key');
END;
