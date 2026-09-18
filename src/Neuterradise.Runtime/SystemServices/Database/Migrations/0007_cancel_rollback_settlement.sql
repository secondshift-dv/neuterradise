-- Track whether a cancelled import's rollback has fully settled.
-- NULL = not cancelled, FALSE = cancel requested but rollback pending/incomplete,
-- TRUE = cancel fully settled (DB delta, Trash, Profile disposition all complete).
ALTER TABLE import_units ADD COLUMN rollback_settled INTEGER NULL;
