using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shoebox.Web.Migrations
{
    /// <summary>
    /// Photos became Media, because the table has held videos since they were added and will
    /// hold whatever comes next. Nothing moves on disk and no row is lost: the table is renamed
    /// in place, gains the two columns that describe what each row is, and Likes follows it.
    /// </summary>
    /// <remarks>
    /// Written by hand. Scaffolding this produced a DropTable/CreateTable pair, which is how EF
    /// renders a rename it can't recognise as one — correct for an empty database, and a way to
    /// lose every photo in a real one.
    /// </remarks>
    public partial class RenamePhotosToMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "Photos" RENAME TO "Media";""");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Photos_PoolId_ContentHash";""");
            migrationBuilder.Sql(
                """CREATE INDEX "IX_Media_PoolId_ContentHash" ON "Media" ("PoolId", "ContentHash");""");

            migrationBuilder.Sql("""ALTER TABLE "Media" ADD COLUMN "Kind" INTEGER NOT NULL DEFAULT 0;""");
            migrationBuilder.Sql("""ALTER TABLE "Media" ADD COLUMN "HasAnimation" INTEGER NOT NULL DEFAULT 0;""");

            // Kind was inferred from the content type until it had a column of its own.
            migrationBuilder.Sql("""UPDATE "Media" SET "Kind" = 1 WHERE "ContentType" LIKE 'video/%';""");

            // SQLite can't repoint a foreign key in place, so Likes is rebuilt around the new
            // column name. Recreated by hand for the same reason as above: a scaffolded rebuild
            // copies rows through the *new* schema, which this table doesn't have yet.
            migrationBuilder.Sql(
                """
                CREATE TABLE "Likes_new" (
                    "MediaId" TEXT NOT NULL,
                    "UploaderUid" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "PK_Likes" PRIMARY KEY ("MediaId", "UploaderUid"),
                    CONSTRAINT "FK_Likes_Media_MediaId" FOREIGN KEY ("MediaId")
                        REFERENCES "Media" ("Id") ON DELETE CASCADE
                );
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO "Likes_new" ("MediaId", "UploaderUid", "CreatedAt")
                SELECT "PhotoId", "UploaderUid", "CreatedAt" FROM "Likes";
                """);
            migrationBuilder.Sql("""DROP TABLE "Likes";""");
            migrationBuilder.Sql("""ALTER TABLE "Likes_new" RENAME TO "Likes";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The table comes back first: rebuilding Likes around a foreign key means the table
            // it points at has to be there under that name already.
            migrationBuilder.Sql("""ALTER TABLE "Media" DROP COLUMN "HasAnimation";""");
            migrationBuilder.Sql("""ALTER TABLE "Media" DROP COLUMN "Kind";""");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Media_PoolId_ContentHash";""");
            migrationBuilder.Sql("""ALTER TABLE "Media" RENAME TO "Photos";""");
            migrationBuilder.Sql(
                """CREATE INDEX "IX_Photos_PoolId_ContentHash" ON "Photos" ("PoolId", "ContentHash");""");

            migrationBuilder.Sql(
                """
                CREATE TABLE "Likes_old" (
                    "PhotoId" TEXT NOT NULL,
                    "UploaderUid" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "PK_Likes" PRIMARY KEY ("PhotoId", "UploaderUid"),
                    CONSTRAINT "FK_Likes_Photos_PhotoId" FOREIGN KEY ("PhotoId")
                        REFERENCES "Photos" ("Id") ON DELETE CASCADE
                );
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO "Likes_old" ("PhotoId", "UploaderUid", "CreatedAt")
                SELECT "MediaId", "UploaderUid", "CreatedAt" FROM "Likes";
                """);
            migrationBuilder.Sql("""DROP TABLE "Likes";""");
            migrationBuilder.Sql("""ALTER TABLE "Likes_old" RENAME TO "Likes";""");
        }
    }
}
