using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Data;

// SQL definitions live with the model so deleting Migrations loses no schema objects.
// Keep the SQL in annotations: snapshots freeze each version for future upgrades and rollbacks.
internal static class PostgreSqlSchema
{
    internal const string CreateAnnotation = "CloneDeploy:CreateSchemaSql";
    internal const string DropAnnotation = "CloneDeploy:DropSchemaSql";

    internal static void Configure(ModelBuilder modelBuilder)
    {
        // Source checkout line endings must not cause schema changes across Windows/Linux.
        modelBuilder.HasAnnotation(CreateAnnotation, CreateSql.ReplaceLineEndings("\n"));
        modelBuilder.HasAnnotation(DropAnnotation, DropSql.ReplaceLineEndings("\n"));
    }

    private const string CreateSql = """
        CREATE INDEX "IX_DeviceNics_NormalizedMac" ON "DeviceNics"
        (upper(btrim(replace(replace(replace("Mac", ':', ''), '-', ''), '.', ''))))
        WHERE "Mac" IS NOT NULL;
        CREATE FUNCTION notify_device_activity() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            before_row jsonb;
            after_row jsonb;
            fields text[];
            field text;
            changed boolean := false;
            device_id text;
        BEGIN
            before_row := CASE WHEN TG_OP = 'INSERT' THEN '{}'::jsonb ELSE to_jsonb(OLD) END;
            after_row := CASE WHEN TG_OP = 'DELETE' THEN '{}'::jsonb ELSE to_jsonb(NEW) END;
            fields := CASE TG_TABLE_NAME
                WHEN 'DeviceActivityReports' THEN ARRAY['ConnectionId', 'Kind', 'RunId', 'Name']
                WHEN 'DeviceTasks' THEN ARRAY['Status', 'DeploymentId']
                WHEN 'ActiveImagingTasks' THEN ARRAY['Status', 'ImageName', 'Type', 'DeploymentId', 'UploadId', 'FinishedAtUtc']
                WHEN 'SocketConnections' THEN ARRAY['ConnectionId', 'AgentCertificateId']
                ELSE ARRAY['ArchivedAtUtc', 'ProvisionStatus'] END;
            IF TG_OP <> 'UPDATE' THEN changed := true;
            ELSE
                FOREACH field IN ARRAY fields LOOP
                    IF before_row -> field IS DISTINCT FROM after_row -> field THEN changed := true; EXIT; END IF;
                END LOOP;
                IF TG_TABLE_NAME = 'SocketConnections' AND
                    (before_row ->> 'LastSeenAtUtc')::timestamptz <= now() - interval '90 seconds' THEN
                    changed := true;
                END IF;
            END IF;
            IF changed THEN
                field := CASE WHEN TG_TABLE_NAME = 'Devices' THEN 'Id' ELSE 'DeviceId' END;
                device_id := coalesce(after_row ->> field, before_row ->> field);
                PERFORM pg_notify('device_activity_changed', TG_TABLE_SCHEMA || ':' || device_id);
            END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER device_activity_report_changed AFTER INSERT OR UPDATE OR DELETE ON "DeviceActivityReports"
            FOR EACH ROW EXECUTE FUNCTION notify_device_activity();
        CREATE TRIGGER device_task_activity_changed AFTER INSERT OR UPDATE OR DELETE ON "DeviceTasks"
            FOR EACH ROW EXECUTE FUNCTION notify_device_activity();
        CREATE TRIGGER device_imaging_activity_changed AFTER INSERT OR UPDATE OR DELETE ON "ActiveImagingTasks"
            FOR EACH ROW EXECUTE FUNCTION notify_device_activity();
        CREATE TRIGGER device_connection_activity_changed AFTER INSERT OR UPDATE OR DELETE ON "SocketConnections"
            FOR EACH ROW EXECUTE FUNCTION notify_device_activity();
        CREATE TRIGGER device_record_activity_changed AFTER INSERT OR UPDATE OR DELETE ON "Devices"
            FOR EACH ROW EXECUTE FUNCTION notify_device_activity();
        """;

    private const string DropSql = """
        DROP TRIGGER device_record_activity_changed ON "Devices";
        DROP TRIGGER device_connection_activity_changed ON "SocketConnections";
        DROP TRIGGER device_imaging_activity_changed ON "ActiveImagingTasks";
        DROP TRIGGER device_task_activity_changed ON "DeviceTasks";
        DROP TRIGGER device_activity_report_changed ON "DeviceActivityReports";
        DROP FUNCTION notify_device_activity();
        DROP INDEX "IX_DeviceNics_NormalizedMac";
        """;
}
