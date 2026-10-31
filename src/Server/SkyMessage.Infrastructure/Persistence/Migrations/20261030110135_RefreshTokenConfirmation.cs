using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyMessage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefreshTokenConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refresh_token_confirmed_at",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refresh_token_confirmed_at",
                table: "sessions");
        }
    }
}
