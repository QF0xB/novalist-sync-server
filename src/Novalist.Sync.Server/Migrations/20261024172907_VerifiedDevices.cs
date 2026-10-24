using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class VerifiedDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "linked_by_device_id",
                table: "devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "linking_signature",
                table: "devices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "receiving_public_key",
                table: "devices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "signing_public_key",
                table: "devices",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "linked_by_device_id",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "linking_signature",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "receiving_public_key",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "signing_public_key",
                table: "devices");
        }
    }
}
