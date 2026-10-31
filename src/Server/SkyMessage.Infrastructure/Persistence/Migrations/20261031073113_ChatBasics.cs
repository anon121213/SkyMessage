using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyMessage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatBasics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "protect_content",
                table: "chats",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "protect_content_by",
                table: "chats",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<bool>(
                name: "contact_bar_dismissed",
                table: "chat_members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "pin_order",
                table: "chat_members",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Chats pinned before pin orders existed keep the order they had (newest pin on top): 1..n per user.
            migrationBuilder.Sql("""
                UPDATE chat_members AS m
                SET pin_order = ranked.position
                FROM (
                    SELECT chat_id, user_id,
                           ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY pinned_at NULLS FIRST, chat_id) AS position
                    FROM chat_members
                    WHERE is_pinned
                ) AS ranked
                WHERE m.chat_id = ranked.chat_id AND m.user_id = ranked.user_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "protect_content",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "protect_content_by",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "contact_bar_dismissed",
                table: "chat_members");

            migrationBuilder.DropColumn(
                name: "pin_order",
                table: "chat_members");
        }
    }
}
