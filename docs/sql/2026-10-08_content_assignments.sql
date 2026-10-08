-- Meridian · Main Quest 1 · Department assignments for quizzes and surveys
-- The API creates these tables itself at start-up (AssignmentStore.EnsureSchemaAsync). This script is the same,
-- for running by hand on a server first or for review. Idempotent: safe to run more than once.
-- Run it against the Meridian database (dev: meridian_simple, prod: optimum_meridian).

CREATE TABLE IF NOT EXISTS content_assignments (
  id bigint unsigned NOT NULL AUTO_INCREMENT,
  content_type varchar(10) NOT NULL,           -- 'quiz' (content_id = quizzes.id) or 'survey' (content_id = surveys.id)
  content_id varchar(36) NOT NULL,
  department varchar(200) NOT NULL,            -- department name exactly as on the employee list
  assigned_by_user_id bigint unsigned NULL,
  assigned_at datetime(6) NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_content_assignment (content_type, content_id, department),
  KEY ix_content_assignment_department (department)
) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS content_assignment_settings (
  content_type varchar(10) NOT NULL,
  content_id varchar(36) NOT NULL,
  assigned_only tinyint(1) NOT NULL DEFAULT 0, -- 1 = only the assigned departments can see and take it
  due_on date NULL,                            -- optional due date (South African calendar date)
  updated_by_user_id bigint unsigned NULL,
  updated_at datetime(6) NOT NULL,
  PRIMARY KEY (content_type, content_id)
) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- Handy checks ---------------------------------------------------------------------------------
-- Every assignment with its quiz/survey title:
-- SELECT a.content_type, a.content_id, COALESCE(q.title, s.title) AS title, a.department, st.assigned_only, st.due_on
-- FROM content_assignments a
-- LEFT JOIN quizzes q ON a.content_type = 'quiz'   AND q.id = CAST(a.content_id AS UNSIGNED)
-- LEFT JOIN surveys s ON a.content_type = 'survey' AND s.id = a.content_id
-- LEFT JOIN content_assignment_settings st ON st.content_type = a.content_type AND st.content_id = a.content_id
-- ORDER BY title, a.department;
