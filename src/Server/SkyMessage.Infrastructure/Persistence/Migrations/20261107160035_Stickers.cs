using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyMessage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Stickers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "convert_animation",
                table: "media",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_animation",
                table: "media",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sticker_emoji",
                table: "media",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sticker_id",
                table: "media",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sticker_pack_id",
                table: "media",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "saved_gifs",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_gifs", x => new { x.user_id, x.media_id });
                    table.ForeignKey(
                        name: "fk_saved_gifs_media_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_saved_gifs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sticker_packs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sticker_packs", x => x.id);
                    table.ForeignKey(
                        name: "fk_sticker_packs_users_creator_id",
                        column: x => x.creator_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sticker_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sticker_preferences", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_sticker_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "installed_sticker_packs",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installed_sticker_packs", x => new { x.user_id, x.pack_id });
                    table.ForeignKey(
                        name: "fk_installed_sticker_packs_sticker_packs_pack_id",
                        column: x => x.pack_id,
                        principalTable: "sticker_packs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_installed_sticker_packs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stickers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    emoji = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stickers", x => x.id);
                    table.ForeignKey(
                        name: "fk_stickers_media_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stickers_sticker_packs_pack_id",
                        column: x => x.pack_id,
                        principalTable: "sticker_packs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "favorite_stickers",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sticker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_favorite_stickers", x => new { x.user_id, x.sticker_id });
                    table.ForeignKey(
                        name: "fk_favorite_stickers_stickers_sticker_id",
                        column: x => x.sticker_id,
                        principalTable: "stickers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_favorite_stickers_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recent_stickers",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sticker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recent_stickers", x => new { x.user_id, x.sticker_id });
                    table.ForeignKey(
                        name: "fk_recent_stickers_stickers_sticker_id",
                        column: x => x.sticker_id,
                        principalTable: "stickers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recent_stickers_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_favorite_stickers_sticker_id",
                table: "favorite_stickers",
                column: "sticker_id");

            migrationBuilder.CreateIndex(
                name: "ix_installed_sticker_packs_pack_id",
                table: "installed_sticker_packs",
                column: "pack_id");

            migrationBuilder.CreateIndex(
                name: "ix_recent_stickers_sticker_id",
                table: "recent_stickers",
                column: "sticker_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_gifs_media_id",
                table: "saved_gifs",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_sticker_packs_creator_id_created_at",
                table: "sticker_packs",
                columns: new[] { "creator_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sticker_packs_slug",
                table: "sticker_packs",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stickers_media_id",
                table: "stickers",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_stickers_pack_id_position",
                table: "stickers",
                columns: new[] { "pack_id", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "favorite_stickers");

            migrationBuilder.DropTable(
                name: "installed_sticker_packs");

            migrationBuilder.DropTable(
                name: "recent_stickers");

            migrationBuilder.DropTable(
                name: "saved_gifs");

            migrationBuilder.DropTable(
                name: "sticker_preferences");

            migrationBuilder.DropTable(
                name: "stickers");

            migrationBuilder.DropTable(
                name: "sticker_packs");

            migrationBuilder.DropColumn(
                name: "convert_animation",
                table: "media");

            migrationBuilder.DropColumn(
                name: "is_animation",
                table: "media");

            migrationBuilder.DropColumn(
                name: "sticker_emoji",
                table: "media");

            migrationBuilder.DropColumn(
                name: "sticker_id",
                table: "media");

            migrationBuilder.DropColumn(
                name: "sticker_pack_id",
                table: "media");
        }
    }
}
