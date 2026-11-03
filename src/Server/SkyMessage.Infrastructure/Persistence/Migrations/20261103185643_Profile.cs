using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyMessage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Profile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "birth_day",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "birth_month",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "birth_year",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cover_gradient",
                table: "users",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cover_media_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "links",
                table: "users",
                type: "jsonb",
                nullable: true);

            // Existing accounts show their birthday to contacts, like new ones (PrivacyLevel.Contacts).
            migrationBuilder.AddColumn<int>(
                name: "privacy_birthday",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "profile_color",
                table: "users",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "source",
                table: "contacts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "avatar_photos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chat_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avatar_photos", x => x.id);
                    table.CheckConstraint("ck_avatar_photos_one_owner", "num_nonnulls(user_id, chat_id) = 1");
                    table.ForeignKey(
                        name: "fk_avatar_photos_chats_chat_id",
                        column: x => x.chat_id,
                        principalTable: "chats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_avatar_photos_media_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_avatar_photos_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_cover_media_id",
                table: "users",
                column: "cover_media_id");

            migrationBuilder.CreateIndex(
                name: "ix_avatar_photos_chat_id_media_id",
                table: "avatar_photos",
                columns: new[] { "chat_id", "media_id" },
                unique: true,
                filter: "chat_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_avatar_photos_media_id",
                table: "avatar_photos",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_avatar_photos_user_id_media_id",
                table: "avatar_photos",
                columns: new[] { "user_id", "media_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_users_media_cover_media_id",
                table: "users",
                column: "cover_media_id",
                principalTable: "media",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // No links yet; and the photos people and groups have now start their avatar histories.
            migrationBuilder.Sql("UPDATE users SET links = '[]'::jsonb WHERE links IS NULL;");
            migrationBuilder.Sql("""
                INSERT INTO avatar_photos (id, user_id, chat_id, media_id, set_by_id, set_at)
                SELECT gen_random_uuid(), u.id, NULL, u.avatar_media_id, u.id, u.updated_at
                FROM users AS u
                WHERE u.avatar_media_id IS NOT NULL AND NOT u.is_deleted;

                INSERT INTO avatar_photos (id, user_id, chat_id, media_id, set_by_id, set_at)
                SELECT gen_random_uuid(), NULL, c.id, c.avatar_media_id,
                       COALESCE(c.created_by_id, '00000000-0000-0000-0000-000000000000'::uuid), c.updated_at
                FROM chats AS c
                WHERE c.avatar_media_id IS NOT NULL AND NOT c.is_deleted;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_media_cover_media_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "avatar_photos");

            migrationBuilder.DropIndex(
                name: "ix_users_cover_media_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "birth_day",
                table: "users");

            migrationBuilder.DropColumn(
                name: "birth_month",
                table: "users");

            migrationBuilder.DropColumn(
                name: "birth_year",
                table: "users");

            migrationBuilder.DropColumn(
                name: "cover_gradient",
                table: "users");

            migrationBuilder.DropColumn(
                name: "cover_media_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "links",
                table: "users");

            migrationBuilder.DropColumn(
                name: "privacy_birthday",
                table: "users");

            migrationBuilder.DropColumn(
                name: "profile_color",
                table: "users");

            migrationBuilder.DropColumn(
                name: "source",
                table: "contacts");
        }
    }
}
