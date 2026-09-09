using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services.DataSources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>
/// Backoffice for registering external data sources (API / Data Lake / Local folder / SharePoint)
/// and deciding which employee may read how much of each — Admin only (IT staff hold the Admin
/// role in this system; there is no separate IT role).
///
/// Phase 1: registry and permissions only. Nothing in the chat pipeline reads from a registered
/// source yet — see README 6.22.
/// </summary>
[ApiController]
[Route("api/admin/data-sources")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminDataSourcesController(IDataSourceService dataSources) : ControllerBase
{
    /// <summary>ชนิดแหล่งข้อมูลและขอบเขตสิทธิ์ที่ระบบรู้จัก — frontend ใช้สร้างตัวเลือก</summary>
    [HttpGet("metadata")]
    public IActionResult Metadata() => Ok(new
    {
        SourceTypes = DataSourceTypes.All,
        ScopeTypes = DataSourceScopeTypes.All,
        RequiredConfigKeys = DataSourceConfig.RequiredKeys,
    });

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DataSourceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DataSourceDto>>> GetAll(CancellationToken ct)
        => Ok(await dataSources.GetAllAsync(ct));

    [HttpPost]
    [ProducesResponseType(typeof(DataSourceDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<DataSourceDto>> Create(DataSourceUpsertRequest request, CancellationToken ct)
    {
        DataSourceDto created = await dataSources.CreateAsync(request, User.GetUsername(), ct);
        return CreatedAtAction(nameof(GetAll), new { id = created.SourceId }, created);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(DataSourceDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DataSourceDto>> Update(
        int id, DataSourceUpsertRequest request, CancellationToken ct)
        => Ok(await dataSources.UpdateAsync(id, request, User.GetUsername(), ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await dataSources.DeleteAsync(id, User.GetUsername(), ct);
        return NoContent();
    }

    /// <summary>ทดสอบการเชื่อมต่อจริง — Data Lake จะได้ผลลัพธ์ "ยังไม่มี connector" เสมอ ไม่ใช่ผลลัพธ์ปลอม</summary>
    [HttpPost("{id:int}/test")]
    [ProducesResponseType(typeof(DataSourceTestResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<DataSourceTestResult>> Test(int id, CancellationToken ct)
        => Ok(await dataSources.TestConnectionAsync(id, User.GetUsername(), ct));

    // ---------------------------------------------------------------- สิทธิ์

    /// <summary>ดูสิทธิ์ทั้งหมด กรองได้ตามแหล่งข้อมูลและ/หรือผู้ใช้ (รวมที่เพิกถอนแล้วเพื่อดู history)</summary>
    [HttpGet("grants")]
    [ProducesResponseType(typeof(IReadOnlyList<DataSourceGrantDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DataSourceGrantDto>>> GetGrants(
        [FromQuery] int? sourceId, [FromQuery] int? userId, CancellationToken ct)
        => Ok(await dataSources.GetGrantsAsync(sourceId, userId, ct));

    /// <summary>ให้สิทธิ์ — ถ้าคนนี้มีสิทธิ์ที่เปิดอยู่กับแหล่งข้อมูลนี้แล้ว จะปิดของเดิมแล้วสร้างแถวใหม่แทน</summary>
    [HttpPost("grants")]
    [ProducesResponseType(typeof(DataSourceGrantDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<DataSourceGrantDto>> Grant(DataSourceGrantRequest request, CancellationToken ct)
    {
        DataSourceGrantDto created = await dataSources.GrantAsync(request, User.GetUsername(), ct);
        return CreatedAtAction(nameof(GetGrants), new { }, created);
    }

    /// <summary>เพิกถอนสิทธิ์ — ปิดใช้งาน ไม่ลบแถวทิ้ง เพื่อให้ตรวจสอบย้อนหลังได้ว่าเคยให้ใครไว้ตอนไหน</summary>
    [HttpDelete("grants/{grantId:long}")]
    public async Task<IActionResult> Revoke(long grantId, CancellationToken ct)
    {
        await dataSources.RevokeAsync(grantId, User.GetUsername(), ct);
        return NoContent();
    }
}
