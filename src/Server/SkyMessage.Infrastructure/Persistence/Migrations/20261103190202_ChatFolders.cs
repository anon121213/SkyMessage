using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyMessage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    icon = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    include_contacts = table.Column<bool>(type: "boolean", nullable: false),
                    include_non_contacts = table.Column<bool>(type: "boolean", nullable: false),
                    include_groups = table.Column<bool>(type: "boolean", nullable: false),
                    include_channels = table.Column<bool>(type: "boolean", nullable: false),
                    exclude_muted = table.Column<bool>(type: "boolean", nullable: false),
                    exclude_read = table.Column<bool>(type: "boolean", nullable: false),
                    exclude_archived = table.Column<bool>(type: "boolean", nullable: false),
                    included_chat_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    excluded_chat_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    pinned_chat_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_folders", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_folders_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_list_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archive_new_from_strangers = table.Column<bool>(type: "boolean", nullable: false),
                    keep_archived = table.Column<bool>(type: "boolean", nullable: false),
                    hide_archive = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_list_preferences", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_chat_list_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_folders_user_id_position",
                table: "chat_folders",
                columns: new[] { "user_id", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_folders");

            migrationBuilder.DropTable(
                name: "chat_list_preferences");
        }
    }
}
