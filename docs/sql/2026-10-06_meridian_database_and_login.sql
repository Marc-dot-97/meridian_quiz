-- =============================================================================
-- Meridian: its own database + a limited MySQL login, on the SAME MySQL server as the CRM.
-- Run in MySQL Workbench as an administrator account. DEV first, then PROD (10.10.1.2).
--
-- BEFORE YOU RUN IT:
--   1. Replace BOTH occurrences of  <<TYPE-A-STRONG-PASSWORD>>  with the SAME new password, in your own
--      Workbench copy only. Never commit this file with a password in it and never paste the password in chat.
--   2. The password then goes ONLY into the server's environment variable ConnectionStrings__MeridianDb.
--
-- What it does NOT do: it creates no tables. Meridian creates its own tables the first time it starts
-- (it needs the ALL privileges on optimum_meridian.* granted below for that).
-- It gives Meridian READ-ONLY access to exactly the three CRM tables it needs to know who people are.
-- =============================================================================

CREATE DATABASE IF NOT EXISTS optimum_meridian
  CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- One login, valid when Meridian runs on the MySQL server itself or elsewhere on the 10.10.1.x network.
CREATE USER IF NOT EXISTS 'meridian_app'@'localhost'  IDENTIFIED BY '<<TYPE-A-STRONG-PASSWORD>>';
CREATE USER IF NOT EXISTS 'meridian_app'@'10.10.1.%' IDENTIFIED BY '<<TYPE-A-STRONG-PASSWORD>>';

-- Its own database: full rights (creates/updates its own tables, stores quizzes, attempts, surveys).
GRANT ALL PRIVILEGES ON optimum_meridian.* TO 'meridian_app'@'localhost';
GRANT ALL PRIVILEGES ON optimum_meridian.* TO 'meridian_app'@'10.10.1.%';

-- The CRM's employee data: SELECT only, three tables only.
GRANT SELECT ON optimum_admin.dtbl_employees  TO 'meridian_app'@'localhost';
GRANT SELECT ON optimum_admin.itbl_departments TO 'meridian_app'@'localhost';
GRANT SELECT ON optimum_admin.itbl_jobtitles   TO 'meridian_app'@'localhost';
GRANT SELECT ON optimum_admin.dtbl_employees  TO 'meridian_app'@'10.10.1.%';
GRANT SELECT ON optimum_admin.itbl_departments TO 'meridian_app'@'10.10.1.%';
GRANT SELECT ON optimum_admin.itbl_jobtitles   TO 'meridian_app'@'10.10.1.%';

FLUSH PRIVILEGES;

-- ----------------------------- checks (read-only) ----------------------------
SHOW GRANTS FOR 'meridian_app'@'localhost';
SHOW GRANTS FOR 'meridian_app'@'10.10.1.%';
SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME IN ('optimum_meridian','optimum_admin');
