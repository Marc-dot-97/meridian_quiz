-- =====================================================================================
-- Meridian on PROD (10.10.1.2): database + MySQL login
-- Run in MySQL Workbench, connected to the PROD MySQL server as an ADMIN user.
--
-- RUN IT AS A MYSQL ADMIN (root or another account that may create users) - NOT as 'marc',
-- who only has rights on the CRM databases.
--
-- WHAT YOU DO: the password is NOT stored in this file. In the SAME mysql / Workbench session,
-- FIRST run this one line on its own, with your own new password between the quotes:
--
--        SET @meridian_password := 'your-new-password';
--
--   then run the rest of this file (paste it, or run it as a script).
--   * Use a NEW password for PROD (not the DEV one): 16-64 characters, letters, digits, - or _ only.
--   * If you forget, the script stops with a clear message and creates nothing.
-- WHAT IT DOES:
--   * creates database optimum_meridian (Meridian's own data)
--   * creates login meridian_app for this server only (localhost / 127.0.0.1), nothing remote
--   * ALL rights on optimum_meridian only, and READ-ONLY SELECT on 3 CRM employee tables
-- Nothing in the CRM databases is changed.
-- =====================================================================================

CREATE DATABASE IF NOT EXISTS optimum_meridian
  CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
USE optimum_meridian;

DROP PROCEDURE IF EXISTS _meridian_create_login;
DELIMITER $$
CREATE PROCEDURE _meridian_create_login(IN pw VARCHAR(128))
BEGIN
  IF pw IS NULL OR pw NOT REGEXP '^[A-Za-z0-9_-]{16,64}$' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT =
      'STOP: password not set. Run the SET @meridian_password line first (16-64 chars: letters, digits, - or _). Nothing was created.';
  END IF;

  SET @q = CONCAT('CREATE USER IF NOT EXISTS ''meridian_app''@''localhost'' IDENTIFIED BY ', QUOTE(pw));
  PREPARE s FROM @q; EXECUTE s; DEALLOCATE PREPARE s;
  SET @q = CONCAT('ALTER USER ''meridian_app''@''localhost'' IDENTIFIED BY ', QUOTE(pw));
  PREPARE s FROM @q; EXECUTE s; DEALLOCATE PREPARE s;

  SET @q = CONCAT('CREATE USER IF NOT EXISTS ''meridian_app''@''127.0.0.1'' IDENTIFIED BY ', QUOTE(pw));
  PREPARE s FROM @q; EXECUTE s; DEALLOCATE PREPARE s;
  SET @q = CONCAT('ALTER USER ''meridian_app''@''127.0.0.1'' IDENTIFIED BY ', QUOTE(pw));
  PREPARE s FROM @q; EXECUTE s; DEALLOCATE PREPARE s;
END$$
DELIMITER ;

CALL _meridian_create_login(@meridian_password);
DROP PROCEDURE _meridian_create_login;
SET @meridian_password := NULL;

GRANT ALL PRIVILEGES ON optimum_meridian.* TO 'meridian_app'@'localhost';
GRANT ALL PRIVILEGES ON optimum_meridian.* TO 'meridian_app'@'127.0.0.1';

GRANT SELECT ON optimum_admin.dtbl_employees   TO 'meridian_app'@'localhost';
GRANT SELECT ON optimum_admin.itbl_departments TO 'meridian_app'@'localhost';
GRANT SELECT ON optimum_admin.itbl_jobtitles   TO 'meridian_app'@'localhost';
GRANT SELECT ON optimum_admin.dtbl_employees   TO 'meridian_app'@'127.0.0.1';
GRANT SELECT ON optimum_admin.itbl_departments TO 'meridian_app'@'127.0.0.1';
GRANT SELECT ON optimum_admin.itbl_jobtitles   TO 'meridian_app'@'127.0.0.1';

-- Checks: expect exactly 2 accounts (localhost, 127.0.0.1), each with ALL on optimum_meridian + 3 SELECT lines
SELECT user, host, account_locked FROM mysql.user WHERE user = 'meridian_app';
SHOW GRANTS FOR 'meridian_app'@'localhost';
SHOW GRANTS FOR 'meridian_app'@'127.0.0.1';
