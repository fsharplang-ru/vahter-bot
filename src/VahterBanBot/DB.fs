module VahterBanBot.DB

open Npgsql
open VahterBanBot.Types
open Dapper
open VahterBanBot.Utils

let private connString = getEnv "DATABASE_URL"

let upsertUser (user: User) =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO "user" (id, username)
VALUES (@id, @username)
ON CONFLICT (id) DO UPDATE
    SET username   =
            CASE
                WHEN EXCLUDED.username != "user".username THEN EXCLUDED.username
                ELSE "user".username
                END,
        updated_at =
            CASE
                WHEN EXCLUDED.username != "user".username THEN timezone('utc'::TEXT, NOW())
                ELSE "user".updated_at
                END
RETURNING *;
"""

        let! insertedUser =
            conn.QueryAsync<User>(
                sql,
                {| id = user.Id; username = user.Username |}
            )

        return insertedUser |> Seq.head
    }
