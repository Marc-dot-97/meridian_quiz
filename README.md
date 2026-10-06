# Meridian Simple

A fresh Meridian project with email/password registration and sign-in, MySQL persistence, quiz and survey builders, scheduled quizzes, results and personal Excel export.

## Updating your working Meridian Simple project

1. Stop Meridian in Visual Studio and extract this package to a new folder.
2. Copy your working MySQL connection details into `src/Meridian.Api/appsettings.json`. Keep the SAME `meridian_simple` database to retain your registered users, quizzes and surveys.
3. Open `Meridian.slnx`. Set **Meridian.Api** as the only startup project and select **https**. Rebuild and run.
4. No SQL scripts are required. Startup adds nullable `delete_after` columns and indexes to `quizzes` and `surveys` if missing. Your MySQL account needs ALTER permission for this one-time upgrade. Existing records receive NULL and are kept indefinitely.
5. Sign in with your existing account at https://localhost:7351.

## Automatic deletion: Add to archive

- Both creation forms contain **Add to archive**, checked by default.
- Checked: the API saves `delete_after = created_at + 24 calendar months` in UTC. It is measured from creation, not unlock, expiry, last use or completion.
- Unchecked before saving: `delete_after` is NULL and the item is not automatically deleted. The normal quiz expiry setting still works independently.
- Despite the requested label, this is **permanent deletion**, not a recycle bin or recoverable archive. Each checkbox explains this in the form.
- Cleanup runs before the API starts serving requests and every five minutes while it runs. If the API is off at the deadline, cleanup catches up on the next startup. No browser session is required. An already-open page updates on its next reload/request.
- A due quiz is deleted along with its attempts, saved answers/results, linked legacy CPD entries, quiz-question links and exclusive questions/answer choices. Questions shared with another quiz are retained. Legacy survey responses linked to deleted attempts are also removed.
- A due survey is deleted along with its definition, responses and completions. Legacy active-survey selection is cleared.
- Historical totals, rankings and reports derived from deleted attempts may change. User accounts, unrelated quizzes/surveys and shared categories remain.
- Each item is deleted in a database transaction; failed background cleanup is logged and retried at the next interval. Multiple API instances coordinate cleanup with a database lock.
- Existing quizzes/surveys are not retroactively opted in. This update adds the choice during creation; it does not add editing controls for previously saved items.

## Start here (fresh installation)

Requirements: .NET 10 SDK and a running MySQL 8.4 server.

1. Extract this ZIP to a new folder. Do not copy it over your previous project.
2. Open `src/Meridian.Api/appsettings.json`. Replace `CHANGE_ME` with your MySQL password and adjust the server/user if needed. Keep `Database=meridian_simple`. The MySQL account needs permission to create this database and its tables. You can instead supply the connection string using .NET user secrets or `ConnectionStrings__MeridianDb`.
3. In a terminal in this folder run:

```sh
dotnet dev-certs https --trust
dotnet run --project src/Meridian.Api --launch-profile https
```

4. Open **https://localhost:7351**. Choose **Create an account**, enter your details and a password of at least 8 characters. Registration signs you in. On later visits, use that email and password.

**No SQL scripts need to be run.** The application creates the new database/tables on its first successful start. Do not run `05_mysql_accounts_and_attempts.sql` for this project. Your old database and accounts are separate; register a new account here.

In Visual Studio with .NET 10 support, open `Meridian.slnx`, set **Meridian.Api** as the only startup project and select **https**. The API serves the Blazor client, so you do not need a second launch profile or a separate client URL.

## Included behavior

- Server-side email/password verification with ASP.NET password hashing and an HTTPS-only, HttpOnly session cookie.
- Standard registration; no setup code, administrator approval, Microsoft identity registration, or email service required.
- Email format is validated. This version does not send an email confirmation link or provide password recovery by email.
- All registered users may create quizzes and surveys. This is a simple shared-author application; department and job-title selections do not grant administrator access.
- Quizzes, attempts, completed results and survey responses are stored in MySQL. A fresh database starts empty.
- Quiz availability/expiry, server-side scoring and owner-only attempt access are retained. Reloading an active quiz can start a new attempt; completed results remain stored.
- Personal report export includes the signed-in user's completed attempts for the selected period.

## Troubleshooting

- **Access denied / cannot connect:** check that MySQL is running and check the connection string password, port and database creation permissions. MySQL credentials are different from the email/password you register in Meridian.
- **Email or password is incorrect:** use an account created in this fresh version. Old profiles are not imported.
- **Email already registered:** sign in with the password used when creating that account.
- **Certificate error:** trust the .NET development HTTPS certificate and use the HTTPS URL above. HTTPS is required for the session cookie.
- **Too many attempts:** wait one minute before trying again.
- **Database already contains unrelated tables:** use a new, empty `meridian_simple` database. Automatic creation initializes an empty database; only the retention columns described above are automatically added to an existing Meridian Simple schema.

Keep the MySQL password out of source control. For deployment, use a valid HTTPS certificate and persistent ASP.NET Data Protection keys so sessions survive application restarts.
