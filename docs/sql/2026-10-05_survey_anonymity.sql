-- =====================================================================================
-- Meridian · Survey anonymity (surveys ONLY)                         2026-10-05
-- Database: meridian_simple (MySQL 8.0.14+ / 9.x)
--
-- WHY
--   Before this change, survey_completions held user_id AND answers_json in the same
--   row, so answers were traceable to a person. This splits them:
--     Split 1  survey_completions        WHO took part (user_id), plus the person's own
--                                        private copy used only by their personal report.
--     Split 2  survey_anonymous_answers  WHAT was answered: random id, department(s) and
--                                        the day only. No user_id, no exact time.
--   Line manager / HR / SuperAdmin reports read split 1 for names and split 2 for results,
--   and hide results when fewer than 3 anonymous responses exist.
--
-- THE API DOES PARTS 1 AND 2 AUTOMATICALLY ON STARTUP (SurveyAnonymity.cs). This script is
-- the reviewable / manual equivalent. Both are idempotent: safe to run more than once.
-- Note: the API's backfill also adds departments from the uploaded employee list; this
-- script uses each user's registered department only.
-- =====================================================================================

USE meridian_simple;

-- -------------------------------------------------------------------------------------
-- PART 1 · Schema
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS survey_anonymous_answers (
  id            char(36)      NOT NULL,              -- random, carries no order or identity
  survey_id     char(36)      NOT NULL,
  departments   varchar(1000) NOT NULL DEFAULT '',   -- "Dept A|Dept B" at time of answering
  answers_json  longtext      NOT NULL,
  submitted_on  date          NOT NULL,              -- day only (SAST), never the exact time
  PRIMARY KEY (id),
  INDEX ix_survey_anonymous_answers_survey (survey_id)
) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- survey_completions.anonymised_at: marks rows already copied (MySQL has no ADD COLUMN IF NOT EXISTS)
SET @has_col := (SELECT COUNT(*) FROM information_schema.columns
                 WHERE table_schema = DATABASE() AND table_name = 'survey_completions' AND column_name = 'anonymised_at');
SET @ddl := IF(@has_col = 0, 'ALTER TABLE survey_completions ADD COLUMN anonymised_at datetime(6) NULL', 'SELECT ''anonymised_at already exists''');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- -------------------------------------------------------------------------------------
-- PART 2 · Backfill existing responses into the anonymous table (one transaction)
-- -------------------------------------------------------------------------------------
START TRANSACTION;

INSERT INTO survey_anonymous_answers (id, survey_id, departments, answers_json, submitted_on)
SELECT
  -- random UUID-shaped id (UUID() is time-based and would leak the order of answers)
  LOWER(INSERT(INSERT(INSERT(INSERT(HEX(RANDOM_BYTES(16)), 9, 0, '-'), 14, 0, '-'), 19, 0, '-'), 24, 0, '-')),
  c.survey_id,
  REGEXP_REPLACE(u.department, '^[A-Za-z]{2,6}[[:space:]]+-[[:space:]]+', ''),
  c.answers_json,
  DATE(c.submitted_at + INTERVAL 2 HOUR)            -- South African day
FROM survey_completions c
JOIN users u ON u.id = c.user_id
WHERE c.anonymised_at IS NULL
ORDER BY RAND();

UPDATE survey_completions SET anonymised_at = UTC_TIMESTAMP(6) WHERE anonymised_at IS NULL;

COMMIT;

-- Check: every completion should now be anonymised
SELECT COUNT(*) AS completions,
       SUM(anonymised_at IS NOT NULL) AS anonymised,
       (SELECT COUNT(*) FROM survey_anonymous_answers) AS anonymous_rows
FROM survey_completions;

-- =====================================================================================
-- PART 3 · Report queries (READ-ONLY). Set the two variables first.
-- =====================================================================================
-- List surveys to pick an id:
SELECT id, title, created_at FROM surveys ORDER BY created_at DESC;

SET @survey_id  := '5eed0000-0000-4000-8000-000000000001';   -- paste a survey id from the list above
SET @department := NULL;   -- e.g. 'Optimum Shared Services (Pty) Ltd'; NULL = all departments

-- Split 1 · WHO took part / did not (named; reads no answers)
SELECT u.display_name,
       REGEXP_REPLACE(u.department, '^[A-Za-z]{2,6}[[:space:]]+-[[:space:]]+', '') AS department,
       CASE WHEN c.id IS NULL THEN 'Did not take part' ELSE 'Took part' END       AS status,
       c.submitted_at
FROM users u
LEFT JOIN survey_completions c ON c.user_id = u.id AND c.survey_id = @survey_id
WHERE u.is_active = 1
  AND (@department IS NULL
       OR REGEXP_REPLACE(u.department, '^[A-Za-z]{2,6}[[:space:]]+-[[:space:]]+', '') = @department)
ORDER BY status DESC, u.display_name;

-- Split 2a · How many anonymous responses (only share results when this is 3 or more)
SELECT COUNT(*) AS anonymous_responses
FROM survey_anonymous_answers r
WHERE r.survey_id = @survey_id
  AND (@department IS NULL OR CONCAT('|', r.departments, '|') LIKE CONCAT('%|', @department, '|%'));

-- Split 2b · WHAT was answered: count and % per option, per multiple-choice question (anonymous)
--   One JSON_TABLE with NESTED PATH reads questions and their options together
--   (MySQL rejects a second JSON_TABLE over a JSON column produced by the first: error 1210).
WITH options AS (
  SELECT q.question_no, q.question_id, q.question, q.option_no - 1 AS choice_index, q.option_text
  FROM surveys s,
       JSON_TABLE(s.definition_json, '$.Questions[*]' COLUMNS (
         question_no  FOR ORDINALITY,
         question_id  char(36)      PATH '$.Id',
         question     varchar(2000) PATH '$.Text',
         qtype        int           PATH '$.Type',      -- 0 multiple choice, 1 short text, 2 rating
         NESTED PATH '$.Options[*]' COLUMNS (
           option_no   FOR ORDINALITY,
           option_text varchar(500) PATH '$'))) q
  WHERE s.id = @survey_id AND q.qtype = 0 AND q.option_no IS NOT NULL
),
answers AS (
  SELECT a.question_id, a.choice_index
  FROM survey_anonymous_answers r,
       JSON_TABLE(r.answers_json, '$[*]' COLUMNS (
         question_id  char(36) PATH '$.QuestionId',
         choice_index int      PATH '$.ChoiceIndex')) a
  WHERE r.survey_id = @survey_id
    AND (@department IS NULL OR CONCAT('|', r.departments, '|') LIKE CONCAT('%|', @department, '|%'))
)
SELECT o.question_no,
       o.question,
       o.option_text,
       COUNT(a.question_id) AS picked,
       ROUND(100 * COUNT(a.question_id)
             / NULLIF(SUM(COUNT(a.question_id)) OVER (PARTITION BY o.question_no), 0), 1) AS percent
FROM options o
LEFT JOIN answers a ON a.question_id = o.question_id AND a.choice_index = o.choice_index
GROUP BY o.question_no, o.question, o.choice_index, o.option_text
ORDER BY o.question_no, o.choice_index;

-- Split 2c · Rating questions: count and % per star (anonymous)
WITH questions AS (
  SELECT q.* FROM surveys s,
       JSON_TABLE(s.definition_json, '$.Questions[*]' COLUMNS (
         question_no FOR ORDINALITY, question_id char(36) PATH '$.Id',
         question varchar(2000) PATH '$.Text', qtype int PATH '$.Type')) q
  WHERE s.id = @survey_id AND q.qtype = 2
),
ratings AS (
  SELECT a.question_id, a.rating
  FROM survey_anonymous_answers r,
       JSON_TABLE(r.answers_json, '$[*]' COLUMNS (question_id char(36) PATH '$.QuestionId', rating int PATH '$.Rating')) a
  WHERE r.survey_id = @survey_id AND a.rating BETWEEN 1 AND 5
    AND (@department IS NULL OR CONCAT('|', r.departments, '|') LIKE CONCAT('%|', @department, '|%'))
)
SELECT q.question_no, q.question, x.rating AS stars, COUNT(x.rating) AS picked,
       ROUND(100 * COUNT(x.rating) / NULLIF(SUM(COUNT(x.rating)) OVER (PARTITION BY q.question_no), 0), 1) AS percent
FROM questions q
LEFT JOIN ratings x ON x.question_id = q.question_id
GROUP BY q.question_no, q.question, x.rating
ORDER BY q.question_no, x.rating DESC;
