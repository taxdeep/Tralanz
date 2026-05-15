# Tralanz 生产交付 - 详细执行计划

**目标**：从当前状态 (Phase 0: 核心逻辑✓ 但安全缺口) 升级到 Phase 2 (生产就绪)
**时间**: 6周，按优先级分4个迭代
**策略**: 逻辑严谨 (清晰依赖) + UI统一 (严格设计系统) + 系统安全 (多层防护)

---

## 📊 **总体依赖图**

```
                    错误码体系 (Week 1)
                          ↓
        ┌─────────────────┼─────────────────┐
        ↓                 ↓                 ↓
   健康检查          并发/幂等            Frankfurter
   (Week 1)         (Weeks 2-3)          熔断 (Week 3)
        ↓                 ↓                 ↓
        └─────────────────┼─────────────────┘
                          ↓
                 授权中间件 (Week 4)
                          ↓
            ┌─────────────┼──────────────┐
            ↓             ↓              ↓
        数据掩码      超时配置        审计日志
        (Week 4)     (Week 4)       (Week 5)
```

**关键依赖**: ❌ 无错误码体系 = 所有功能无法正确返回错误
→ **必须首先实现**

---

## 🔧 **第0周 (准备期) — 2天**

### 任务 0.1: 创建项目管理器
**目的**: 跟踪实现进度
**执行**:
- [ ] 创建 `IMPLEMENTATION_TRACKER.md` 在根目录
- [ ] 在 backend/src 中建立 feature branches:
  - `feature/error-codes`
  - `feature/concurrency-safety`
  - `feature/frankfurter-resilience`
  - `feature/auth-middleware`
  - `feature/data-masking`

### 任务 0.2: 数据库准备
**目的**: 为并发控制做数据库准备
**执行**:
- [ ] 审查现有 `ar_open_items` 和 `ap_open_items` 表结构
- [ ] 规划新列:
  ```sql
  ALTER TABLE ar_open_items ADD COLUMN IF NOT EXISTS 
    locked_by_idempotency_key UUID UNIQUE,
    locked_until_utc TIMESTAMP;
  ALTER TABLE ap_open_items ADD COLUMN IF NOT EXISTS 
    locked_by_idempotency_key UUID UNIQUE,
    locked_until_utc TIMESTAMP;
  ```
- [ ] 创建测试数据库快照用于压力测试

### 任务 0.3: 环境准备
**目的**: 配置开发/测试环境
**执行**:
- [ ] 验证 .NET 10.0 SDK 和 PostgreSQL 13+ 可用
- [ ] 配置 Redis (本地 Docker):
  ```bash
  docker run -d -p 6379:6379 redis:latest
  ```
- [ ] 设置 Sentry 项目 (用于错误追踪)
- [ ] 创建 `appsettings.Development.json` 扩展条目:
  ```json
  {
    "Logging": { "LogLevel": { "Default": "Debug" } },
    "Features": {
      "EnableErrorCodeStructure": false,  // 稍后启用
      "EnableConcurrencyLocking": false,
      "EnableHealthChecks": false
    }
  }
  ```

**验收**: `dotnet build` 成功，Redis 连接正常，所有设置已读取

---

## 🔴 **第1周 — PHASE 1.0: 基础设施 & 错误体系**

### 任务 1.1: 创建领域异常体系 (2天)
**核心概念**: 所有后续功能都依赖结构化错误响应

**文件架构**:
```
backend/src/SharedKernel/
├── Exceptions/
│   ├── PostingException.cs                  (Abstract base)
│   ├── PostingPeriodException.cs           (Period locked/closed)
│   ├── ConcurrencyException.cs             (Race condition)
│   ├── ExternalServiceException.cs         (Frankfurter down)
│   ├── ValidationException.cs              (Input validation)
│   ├── AuthorizationException.cs           (Permission denied)
│   └── ExceptionExtensions.cs              (ToApiResponse)
├── Models/
│   └── ApiErrorResponse.cs                 (Unified response)
```

**实现细节**:

```csharp
// Post: PostingException.cs
public abstract class PostingException : ApplicationException
{
    public string ErrorCode { get; init; }           // "POSTING_PERIOD_LOCKED"
    public string ErrorCategory { get; init; }       // "Transient" or "Permanent"
    public Dictionary<string, object> Context { get; init; }
    
    public int HttpStatusCode => ErrorCategory switch
    {
        "Transient" => 503,    // Service Unavailable (retry)
        "Permanent" => 400,    // Bad Request (don't retry)
        "Conflict" => 409,     // Conflict (check state)
        "NotFound" => 404,
        _ => 500
    };
}

public sealed class PostingPeriodException : PostingException
{
    public static PostingPeriodException PeriodLocked(
        CompanyId company, string period, DateTimeOffset lockedUntil)
    {
        return new PostingPeriodException
        {
            ErrorCode = "POSTING_PERIOD_LOCKED",
            ErrorCategory = "Transient",   // 稍后会开放
            Context = new()
            {
                ["company_id"] = company.Value,
                ["period"] = period,
                ["locked_until_utc"] = lockedUntil.UtcDateTime
            },
            Message = $"Posting period {period} is locked until {lockedUntil:O}"
        };
    }
}

public sealed class ConcurrencyException : PostingException
{
    public static ConcurrencyException DuplicatePayment(
        CompanyId company, InvoiceId invoice, decimal appliedAmount)
    {
        return new ConcurrencyException
        {
            ErrorCode = "DUPLICATE_PAYMENT_DETECTED",
            ErrorCategory = "Conflict",
            Context = new()
            {
                ["company_id"] = company.Value,
                ["invoice_id"] = invoice.Value,
                ["applied_amount_masked"] = "***"  // 🔐 敏感数据掩码
            },
            Message = "Concurrent payment detected. Reload and retry."
        };
    }
}

// Post: ApiErrorResponse.cs
public sealed record ApiErrorResponse(
    string ErrorCode,
    string Message,
    string ErrorCategory,  // "Transient" | "Permanent" | "Conflict"
    Dictionary<string, object> Context,
    string CorrelationId,  // 追踪 Sentry
    DateTimeOffset Timestamp
)
{
    public static ApiErrorResponse FromException(PostingException ex, string correlationId)
    {
        return new(
            ErrorCode: ex.ErrorCode,
            Message: ex.Message,
            ErrorCategory: ex.ErrorCategory,
            Context: ex.Context,
            CorrelationId: correlationId,
            Timestamp: DateTimeOffset.UtcNow
        );
    }
}
```

**修改 Program.cs**:
```csharp
// Line 930: 替换现有异常处理
app.Map("/accounting/{*path}", async ctx =>
{
    try
    {
        await ctx.Response.StartAsync();
    }
    catch (PostingException ex)
    {
        var correlationId = ctx.TraceIdentifier;
        var response = ApiErrorResponse.FromException(ex, correlationId);
        ctx.Response.StatusCode = ex.HttpStatusCode;
        await ctx.Response.WriteAsJsonAsync(response);
        
        // 记录到 Sentry，带分类
        SentrySdk.CaptureException(ex, scope =>
        {
            scope.SetTag("error_code", ex.ErrorCode);
            scope.SetTag("error_category", ex.ErrorCategory);
        });
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new
        {
            message = "Internal server error",
            correlationId = ctx.TraceIdentifier
        });
    }
});
```

**测试用例** (`Tests/SharedKernel.Tests/ExceptionTests.cs`):
```csharp
[Fact]
public void PostingPeriodException_ShouldMaskSensitiveData()
{
    var ex = PostingPeriodException.PeriodLocked(
        CompanyId.From(Guid.NewGuid()),
        "2024-Q2",
        DateTimeOffset.UtcNow.AddDays(30)
    );
    
    var response = ApiErrorResponse.FromException(ex, "test-id");
    Assert.NotContains("$", response.Message); // 金额应被掩码
    Assert.Equal("POSTING_PERIOD_LOCKED", response.ErrorCode);
}
```

**检查清单**:
- [ ] 所有异常类都已实现
- [ ] Context 中的敏感数据已掩码 (金额 → "***")
- [ ] HttpStatusCode 映射逻辑正确
- [ ] Sentry 标签捕获完整
- [ ] 单元测试覆盖所有异常路径
- [ ] Swagger 文档更新

---

### 任务 1.2: 创建健康检查端点 (1天)
**目的**: 生产监控的第一步；K8s readiness probe

**文件**:
```
backend/src/Citus.Accounting.Api/
├── HealthChecks/
│   ├── PostgresHealthCheck.cs
│   ├── FrankreuterHealthCheck.cs
│   └── SchemaVersionHealthCheck.cs
└── Endpoints/HealthEndpoints.cs
```

**实现**:
```csharp
// Post: HealthChecks/PostgresHealthCheck.cs
public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly IDbConnectionFactory _factory;
    
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            using var conn = await _factory.OpenAsync(ct);
            var result = await conn.ExecuteScalarAsync("SELECT 1", cancellationToken: ct);
            
            return result == 1
                ? HealthCheckResult.Healthy("PostgreSQL responding normally")
                : HealthCheckResult.Unhealthy("PostgreSQL query returned unexpected result");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection timeout");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"PostgreSQL error: {ex.Message}");
        }
    }
}

// Post: Endpoints/HealthEndpoints.cs
public static class HealthEndpoints
{
    public static void MapHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => true,
            ResponseWriter = ResponseWriter
        });
        
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = ResponseWriter
        });
    }
    
    private static async Task ResponseWriter(HttpContext context, HealthReport report)
    {
        var response = new
        {
            Status = report.Status.ToString(),
            Services = report.Entries.ToDictionary(
                x => x.Key,
                x => new
                {
                    Status = x.Value.Status.ToString(),
                    Description = x.Value.Description,
                    Duration = x.Value.Duration.TotalMilliseconds
                }
            ),
            Timestamp = DateTimeOffset.UtcNow
        };
        
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = report.Status == HealthStatus.Healthy ? 200 : 503;
        await context.Response.WriteAsJsonAsync(response);
    }
}

// Program.cs 注册 (Line 880)
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgresql", tags: new[] { "ready" })
    .AddCheck<FrankurterHealthCheck>("frankfurter", tags: new[] { "ready" })
    .AddCheck<SchemaVersionHealthCheck>("schema", tags: new[] { "ready" });

app.MapHealthChecks();
```

**Kubernetes 部署集成**:
```yaml
# deploy/kubernetes/deployment.yaml (示例)
livenessProbe:
  httpGet:
    path: /health
    port: 8080
  initialDelaySeconds: 30
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 5
```

**测试用例**:
```csharp
[Fact]
public async Task HealthCheck_WithDatabaseDown_ShouldReturn503()
{
    // Arrange: 中断数据库连接
    var check = new PostgresHealthCheck(_factory);
    
    // Act
    var result = await check.CheckHealthAsync(null, CancellationToken.None);
    
    // Assert
    Assert.Equal(HealthStatus.Unhealthy, result.Status);
}
```

**检查清单**:
- [ ] `/health` 返回 200 所有服务可用
- [ ] `/health/ready` 用于 K8s readiness
- [ ] 响应包括每个子系统的状态 + 响应时间
- [ ] 超时配置合理 (5秒以内)
- [ ] 文档化所有端点

---

## ✅ Week 1 验收标准

- [ ] 所有异常类型已实现 (>95% 代码覆盖)
- [ ] API 返回统一 `ApiErrorResponse` 格式
- [ ] 健康检查端点可访问
- [ ] 所有构建通过，无新警告
- [ ] 已为并发修复准备适当的异常类型

**发布检查点**: `feature/error-codes` PR，准备合并到 `main`

---

## 🔒 **第2-3周 — PHASE 2.0: 并发安全 & 幂等性 & Frankfurter弹性**

### 任务 2.1: 开放项目悲观锁定 (3天)
**核心问题**: 支付并发可能重复结算
**解决方案**: SELECT FOR UPDATE + 幂等性键

**修改文件**:
```
backend/src/Citus.Accounting.Infrastructure/
├── Persistence/
│   ├── ArOpenItemRepository.cs          (修改)
│   ├── ApOpenItemRepository.cs          (修改)
│   └── IOpenItemLockManager.cs          (新增)
└── Posting/
    └── DistributedIdempotencyLockService.cs  (新增)
```

**实现**:

```csharp
// Post: Persistence/IOpenItemLockManager.cs
public interface IOpenItemLockManager
{
    /// <summary>
    /// 尝试为幂等请求获取分布式锁
    /// Thread-safe，使用 PostgreSQL FOR UPDATE
    /// </summary>
    Task<bool> TryAcquireLockAsync(
        CompanyId companyId,
        Guid idempotencyKey,
        TimeSpan lockDuration,
        CancellationToken ct);
    
    Task ReleaseLockAsync(CompanyId companyId, Guid idempotencyKey, CancellationToken ct);
}

public sealed class PostgresIdempotencyLockManager : IOpenItemLockManager
{
    private readonly IDbConnectionFactory _factory;
    
    public async Task<bool> TryAcquireLockAsync(
        CompanyId companyId,
        Guid idempotencyKey,
        TimeSpan lockDuration,
        CancellationToken ct)
    {
        try
        {
            using var conn = await _factory.OpenAsync(ct);
            using var tx = conn.BeginTransaction();
            
            // 检查是否已存在相同幂等键的活跃锁定
            var existingLock = await conn.ExecuteScalarAsync<Guid?>(
                @"SELECT locked_by_idempotency_key FROM ar_open_items
                  WHERE locked_by_idempotency_key = @IdempotencyKey
                    AND locked_until_utc > @Now
                  LIMIT 1 FOR UPDATE",  // 🔐 关键：加写锁，防止并发插入
                new { IdempotencyKey = idempotencyKey, Now = DateTimeOffset.UtcNow },
                transaction: tx,
                commandTimeout: 5
            );
            
            if (existingLock.HasValue)
                return false;  // 已被其他线程锁定
            
            // 获取锁成功
            await conn.ExecuteAsync(
                @"UPDATE ar_open_items SET locked_by_idempotency_key = @IdempotencyKey,
                                           locked_until_utc = @LockedUntil
                  WHERE company_id = @CompanyId 
                    AND locked_by_idempotency_key IS NULL
                  LIMIT 1",
                new
                {
                    IdempotencyKey = idempotencyKey,
                    LockedUntil = DateTimeOffset.UtcNow.Add(lockDuration),
                    CompanyId = companyId.Value
                },
                transaction: tx,
                commandTimeout: 5
            );
            
            await tx.CommitAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 锁定超时或请求取消
            throw ConcurrencyException.LockAcquisitionTimeout(companyId, idempotencyKey);
        }
    }
    
    // ... ReleaseLockAsync implementation ...
}

// Post: 修改 ReceivePaymentCommandHandler.cs
public sealed class ReceivePaymentCommandHandler : ICommandHandler<ReceivePaymentCommand>
{
    private readonly IOpenItemLockManager _lockManager;
    private readonly IArOpenItemRepository _arRepository;
    
    public async Task HandleAsync(ReceivePaymentCommand command, CancellationToken ct)
    {
        // Step 1: 尝试获取分布式幂等锁 (5秒超时)
        var idempotencyKey = command.IdempotencyKey ?? 
            Guid.Parse($"{command.DocumentType}:{command.SourceId}");
        
        var lockAcquired = await _lockManager.TryAcquireLockAsync(
            command.CompanyId,
            idempotencyKey,
            TimeSpan.FromSeconds(60),  // 锁定60秒
            ct
        );
        
        if (!lockAcquired)
        {
            // 锁定失败 = 并发请求正在处理此支付
            throw ConcurrencyException.DuplicatePaymentDetected(
                command.CompanyId,
                command.InvoiceId,
                command.AppliedAmount
            );
        }
        
        try
        {
            // Step 2: 进行业务逻辑处理（现有代码）
            // ... posting engine execution ...
            
            // Step 3: 提交事务后释放锁
            await _lockManager.ReleaseLockAsync(
                command.CompanyId,
                idempotencyKey,
                ct
            );
        }
        catch (Exception)
        {
            // 异常时锁定自动过期 (60秒)
            throw;
        }
    }
}
```

**数据库迁移脚本**:
```sql
-- migrations/V003_add_idempotency_locks.sql
ALTER TABLE ar_open_items ADD COLUMN IF NOT EXISTS
  locked_by_idempotency_key UUID UNIQUE,
  locked_until_utc TIMESTAMP;

ALTER TABLE ap_open_items ADD COLUMN IF NOT EXISTS
  locked_by_idempotency_key UUID UNIQUE,
  locked_until_utc TIMESTAMP;

CREATE INDEX IF NOT EXISTS idx_ar_open_items_lock_until
  ON ar_open_items(locked_until_utc) WHERE locked_until_utc IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_ap_open_items_lock_until
  ON ap_open_items(locked_until_utc) WHERE locked_until_utc IS NOT NULL;
```

**竞态条件测试** (关键):
```csharp
[Fact]
public async Task ConcurrentPayments_OnSameInvoice_OnlyOneSucceeds()
{
    // Arrange
    var invoiceId = InvoiceId.From(Guid.NewGuid());
    var command1 = new ReceivePaymentCommand { InvoiceId = invoiceId, AppliedAmount = 500 };
    var command2 = new ReceivePaymentCommand { InvoiceId = invoiceId, AppliedAmount = 600 };
    var lockManager = new PostgresIdempotencyLockManager(_dbFactory);
    
    // Act: 100个并发相同幂等键请求
    var tasks = Enumerable.Range(0, 100)
        .Select(i => lockManager.TryAcquireLockAsync(
            CompanyId.From(Guid.NewGuid()),
            command1.IdempotencyKey,
            TimeSpan.FromSeconds(60),
            CancellationToken.None
        ))
        .ToList();
    
    var results = await Task.WhenAll(tasks);
    
    // Assert: 只有1个成功，其余 99 个失败
    Assert.Equal(1, results.Count(r => r));
    Assert.Equal(99, results.Count(r => !r));
}
```

**检查清单**:
- [ ] `SELECT FOR UPDATE` 不用 NOLOCK (防止脏读)
- [ ] 锁定超时设置合理 (5秒)
- [ ] 异常自动释放锁（避免永久死锁）
- [ ] 负载测试通过 (100+ 并发请求无重复)
- [ ] 数据库索引已优化

---

### 任务 2.2: Frankfurter 熔断器 + 本地缓存 (3天)
**核心问题**: Frankfurter API 是汇率唯一来源
**解决方案**: Polly 熔断器 + Redis 缓存 + 手动覆盖 UI

**文件架构**:
```
backend/src/Citus.Accounting.Infrastructure/
├── Fx/
│   ├── FrankfurterFxRateClient.cs        (修改 - 添加熔断进)
│   ├── FxRateCircuitBreaker.cs            (新增)
│   ├── FxRateCacheService.cs              (新增)
│   └── ManualFxRateOverrideService.cs     (新增)
└── ...

frontend/src/components/
└── FxRateOverrideDialog.razor              (新增 UI)
```

**实现 — 熔断器**:
```csharp
// Post: Fx/FxRateCircuitBreaker.cs
public sealed class FxRateCircuitBreaker
{
    private readonly IAsyncPolicy<Optional<ExchangeRate>> _policy;
    private readonly IFxRateCacheService _cache;
    private readonly IFrankurterFxRateClient _client;
    
    public FxRateCircuitBreaker(
        IFxRateCacheService cache,
        IFrankurterFxRateClient client)
    {
        _cache = cache;
        _client = client;
        
        // Polly 熔断器配置
        var circuitBreakerPolicy = Policy<Optional<ExchangeRate>>
            .Handle<HttpRequestException>()
            .OrResult(r => !r.HasValue)  // 失败 = 返回空值
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3,      // 3次失败后熔断
                durationOfBreak: TimeSpan.FromSeconds(30),  // 30秒熔断期
                onBreak: (outcome, timespan) =>
                {
                    SentrySdk.CaptureMessage(
                        $"FX rate circuit breaker opened for {timespan.TotalSeconds}s",
                        SentryLevel.Warning
                    );
                },
                onReset: () =>
                {
                    SentrySdk.CaptureMessage("FX rate circuit breaker reset", SentryLevel.Info);
                }
            );
        
        // 重试策略 (指数退避)
        var retryPolicy = Policy<Optional<ExchangeRate>>
            .Handle<HttpRequestException>()
            .Or<OperationCanceledException>()
            .OrResult(r => !r.HasValue)
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: i => TimeSpan.FromMilliseconds(100 * Math.Pow(2, i)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    // 重试日志
                }
            );
        
        _policy = Policy.WrapAsync(circuitBreakerPolicy, retryPolicy);
    }
    
    public async Task<Optional<ExchangeRate>> GetRateAsync(
        CurrencyCode fromCurrency,
        CurrencyCode toCurrency,
        DateTimeOffset asOfDate,
        CancellationToken ct)
    {
        return await _policy.ExecuteAsync(async () =>
        {
            // Step 1: 检查缓存
            var cached = await _cache.GetAsync(fromCurrency, toCurrency, asOfDate, ct);
            if (cached.HasValue)
                return cached;
            
            // Step 2: 调用 Frankfurter (受熔断器保护)
            var live = await _client.GetRateAsync(fromCurrency, toCurrency, asOfDate, ct);
            
            if (live.HasValue)
            {
                // 缓存结果 (24小时)
                await _cache.SetAsync(fromCurrency, toCurrency, asOfDate, live.Value, 
                    TimeSpan.FromHours(24), ct);
            }
            else
            {
                // Step 3: Frankfurter 失败，回退到 30 天内最旧的缓存率
                live = await _cache.GetMostRecentAsync(
                    fromCurrency, toCurrency, 
                    asOfDate.AddDays(-30),
                    ct
                );
            }
            
            return live;
        });
    }
}

// Program.cs 注册
builder.Services
    .AddSingleton<IFxRateCacheService, RedisFixRateCacheService>()
    .AddSingleton<IAsyncPolicy<Optional<ExchangeRate>>, FxRateCircuitBreaker.CreatePolicy>()
    .Decorate<IRecommendedFxRateService, FxRateCircuitBreaker>();
```

**实现 — 手动覆盖 UI**:
```csharp
// Post: Commands/OverrideFxRateCommand.cs
public sealed record OverrideFxRateCommand(
    CompanyId CompanyId,
    CurrencyCode FromCurrency,
    CurrencyCode ToCurrency,
    DateTimeOffset AsOfDate,
    decimal ManualRate,
    string OverrideReason      // "Frankfurter Down" | "Regulatory Rate" | etc.
) : ICommand;

// Post: Endpoints/FxRateOverrideEndpoint.cs
public static void MapFxRateOverride(this WebApplication app)
{
    app.MapPost("/accounting/fx-rates/override", async (
        OverrideFxRateCommand command,
        ICommandDispatcher dispatcher,
        CancellationToken ct) =>
    {
        // ✅ 权限检查: 仅 "book_governance" 可覆盖汇率
        var session = ctx.RequestServices.GetRequiredService<BusinessSessionContextAccessor>();
        if (!session.Roles.Contains("book_governance"))
            throw new AuthorizationException("Only governance can override FX rates");
        
        await dispatcher.DispatchAsync(command, ct);
        
        return Results.Ok(new
        {
            Message = "FX rate override applied",
            AuditEntry = new {
                OverriddenAt = DateTimeOffset.UtcNow,
                OverrideReason = command.OverrideReason,
                Rate = command.ManualRate
            }
        });
    })
    .WithName("OverrideFxRate")
    .WithOpenApi()
    .RequireAuthorization();
}
```

**UI 组件 (Blazor)**:
```razor
@* Post: frontend/FxRateOverrideDialog.razor *@
@using Citus.Accounting.Application.Commands

<CitusDrawer @bind-IsOpen="IsOpen" Title="Override FX Rate">
    <EditForm Model="@Command" OnValidSubmit="@OnSubmitAsync">
        <DataAnnotationsValidator />
        
        <div class="citus-form-field">
            <label>From Currency *</label>
            <CitusInput @bind-Value="Command.FromCurrency" Disabled="true" />
        </div>
        
        <div class="citus-form-field">
            <label>To Currency *</label>
            <CitusInput @bind-Value="Command.ToCurrency" Disabled="true" />
        </div>
        
        <div class="citus-form-field">
            <label>Manual Rate (Override) *</label>
            <CitusInput @bind-Value="Command.ManualRate" Type="number" Step="0.0001" />
            <ValidationMessage For="@(() => Command.ManualRate)" />
            <small>Last Frankfurter rate: @CachedRate (D-1 market close)</small>
        </div>
        
        <div class="citus-form-field">
            <label>Override Reason *</label>
            <select @bind="Command.OverrideReason" class="citus-input">
                <option value="">-- Select --</option>
                <option value="Frankfurter Down">Frankfurter API Down</option>
                <option value="Regulatory Rate">Regulatory/Central Bank Rate</option>
                <option value="Internal Policy">Internal Policy Rate</option>
            </select>
            <ValidationMessage For="@(() => Command.OverrideReason)" />
        </div>
        
        <CitusAlert Severity="CitusAlertSeverity.Warning" ShowIcon="true">
            ⚠️ FX rate overrides are audit-logged. Use only when automated rates unavailable.
        </CitusAlert>
        
        <div slot="footer" class="citus-drawer-footer">
            <CitusButton Variant="CitusButtonVariant.Ghost" OnClick="@IsOpen.Toggle">
                Cancel
            </CitusButton>
            <CitusButton Type="submit" Variant="CitusButtonVariant.Primary">
                Apply Override
            </CitusButton>
        </div>
    </EditForm>
</CitusDrawer>

@code {
    [Parameter] public bool IsOpen { get; set; }
    
    private OverrideFxRateCommand Command = new(/*...*/);
    private decimal CachedRate;
    
    protected override async Task OnParametersSetAsync()
    {
        // 获取最后一个已知的汇率（用于参考）
        CachedRate = await FxRateService.GetMostRecentAsync(/*...*/);
    }
    
    private async Task OnSubmitAsync()
    {
        await CommandDispatcher.DispatchAsync(Command);
        IsOpen = false;
        // 触发 parent 刷新汇率列表
    }
}
```

**实现 — Redis 缓存**:
```csharp
// Post: Fx/FxRateCacheService.cs
public interface IFxRateCacheService
{
    Task<Optional<ExchangeRate>> GetAsync(
        CurrencyCode from, CurrencyCode to, DateTimeOffset asOf, CancellationToken ct);
    
    Task SetAsync(
        CurrencyCode from, CurrencyCode to, DateTimeOffset asOf, ExchangeRate rate,
        TimeSpan ttl, CancellationToken ct);
    
    Task<Optional<ExchangeRate>> GetMostRecentAsync(
        CurrencyCode from, CurrencyCode to, DateTimeOffset sinceDate, CancellationToken ct);
}

public sealed class RedisFxRateCacheService : IFxRateCacheService
{
    private readonly IConnectionMultiplexer _redis;
    private const string KeyTemplate = "fx:{from}:{to}:{date:yyyy-MM-dd}";
    
    public async Task<Optional<ExchangeRate>> GetAsync(
        CurrencyCode from, CurrencyCode to, DateTimeOffset asOf, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var key = KeyTemplate
            .Replace("{from}", from.Value)
            .Replace("{to}", to.Value)
            .Replace("{date}", asOf.Date.ToString("yyyy-MM-dd"));
        
        var value = await db.StringGetAsync(key);
        return value.HasValue 
            ? JsonSerializer.Deserialize<ExchangeRate>(value.ToString())
            : Optional<ExchangeRate>.None;
    }
    
    // ... 其他方法 ...
}
```

**检查清单**:
- [ ] Polly 熔断器配置正确 (3 次失败 → 30 秒熔断)
- [ ] Redis 缓存 TTL = 24小时
- [ ] 后备逻辑: 30 天内最旧缓存率
- [ ] 手动覆盖仅 book_governance 可用，完全审计
- [ ] UI 符合 CitusDesignLanguage (Drawer + Alert + Validation)
- [ ] 负载测试: Frankfurter 宕机 → 使用缓存成功发布

---

## 🏁 Week 3 验收标准

- [ ] 开放项目锁定并发测试通过 (>99% 单结)
- [ ] Frankfurter 下线 → 系统使用缓存继续工作
- [ ] 手动汇率覆盖 UI 可用 + 审计完整
- [ ] 所有合并到 `main`，准备第 4 周工作

---

## 🔐 **第4周 — PHASE 3.0: 授权 & 数据保护 & 超时**

### 任务 3.1: 中央授权中间件 (1天)
**目的**: 集中管理 [Authorize] 检查，防止遗漏认证

**文件**:
```
backend/src/Citus.Accounting.Api/
├── Middleware/
│   └── AuthorizationMiddleware.cs        (新增)
└── Program.cs                            (修改)
```

**实现**:
```csharp
// Post: Middleware/AuthorizationMiddleware.cs
public sealed class CentralAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CentralAuthorizationMiddleware> _logger;
    
    // 白名单: 无需认证的端点
    private static readonly HashSet<string> UnprotectedPaths = new()
    {
        "/health",
        "/health/ready",
        "/swagger",
        "/swagger/v1/swagger.json",
        "/metrics"  // Prometheus
    };
    
    public CentralAuthorizationMiddleware(RequestDelegate next, ILogger<CentralAuthorizationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }
    
    public async Task InvokeAsync(HttpContext ctx, BusinessSessionContextAccessor sessionAccessor)
    {
        var path = ctx.Request.Path.Value;
        
        // Step 1: 检查是否在白名单中
        if (UnprotectedPaths.Any(up => path.StartsWith(up)))
        {
            await _next(ctx);
            return;
        }
        
        // Step 2: 对所有 /accounting 端点强制认证
        if (path.StartsWith("/accounting"))
        {
            // 验证会话已设置 (由 BusinessRequestContractGuard 完成)
            if (sessionAccessor.Session == null)
            {
                _logger.LogWarning("Rejected unauth request to {Path}", path);
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    ErrorCode = "UNAUTHENTICATED",
                    Message = "Missing authentication context"
                });
                return;
            }
            
            // Step 3: 对写操作 (POST/PUT/DELETE) 额外检查
            if (ctx.Request.Method != HttpMethod.Get.Method)
            {
                // 某些操作需要特定角色
                var requiredRoles = GetRequiredRolesForPath(path);
                var userRoles = sessionAccessor.Session.Roles;
                
                if (!userRoles.Intersect(requiredRoles).Any())
                {
                    _logger.LogWarning(
                        "Unauthorized: {User} tried {Method} {Path}",
                        sessionAccessor.Session.UserId,
                        ctx.Request.Method,
                        path
                    );
                    
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        ErrorCode = "INSUFFICIENT_PERMISSIONS",
                        Message = $"Requires one of: {string.Join(", ", requiredRoles)}"
                    });
                    return;
                }
            }
        }
        
        await _next(ctx);
    }
    
    private static SortedSet<string> GetRequiredRolesForPath(string path) =>
        path switch
        {
            // 会计发布操作
            var p when p.Contains("/post") => new() { "book_governance", "accounting" },
            
            // 批准工作流
            var p when p.Contains("/approve") => new() { "approve", "book_governance" },
            
            // 审计访问
            var p when p.Contains("/audit") => new() { "audit", "owner" },
            
            // 默认: 只读访问
            _ => new() { "accounting", "book_governance", "audit", "owner" }
        };
}

// Program.cs 注册 (Line 890)
app.UseMiddleware<CentralAuthorizationMiddleware>();
```

**测试**:
```csharp
[Fact]
public async Task AuthorizationMiddleware_WithoutValidSession_Returns401()
{
    // Arrange
    var ctx = new DefaultHttpContext();
    ctx.Request.Path = "/accounting/invoices";
    
    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => middleware.InvokeAsync(ctx, invalidSessionAccessor)
    );
}
```

---

### 任务 3.2: 数据库超时 & 连接池配置 (1天)
**目的**: 防止查询饥荒、连接耗尽

**修改 Program.cs**:
```csharp
// Line 750: 数据库配置
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddNpgsqlDataSource(
    connectionString,
    dataSourceBuilder =>
    {
        // 连接池配置
        dataSourceBuilder
            .ConnectionStringBuilder.CommandTimeout = 30;  // 🔐 全局查询超时 30秒
        
        // 最大连接数: 50 (default 100)
        dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 50;
        
        // 最小连接数: 10
        dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 10;
        
        // 连接空闲超时: 15 分钟
        dataSourceBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = TimeSpan.FromMinutes(15);
        
        // 连接生命周期: 30 分钟 (防止长期连接泄漏)
        dataSourceBuilder.ConnectionStringBuilder.ConnectionLifetime = TimeSpan.FromMinutes(30);
        
        // 设置查询超时 (已通过CommandTimeout 设置)
    }
);

// 为不同用途创建命名连接池 (可选，高级)
// 避免审计日志读取耗尽发布引擎连接
builder.Services
    .AddNamedNpgsqlDataSource("writing",
        dataSourceBuilder =>
        {
            dataSourceBuilder.ConnectionStringBuilder.ApplicationName = "accounting-write";
            dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 30;
        }
    )
    .AddNamedNpgsqlDataSource("reading",
        dataSourceBuilder =>
        {
            dataSourceBuilder.ConnectionStringBuilder.ApplicationName = "accounting-read";
            dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 20;
        }
    );
```

**连接池监控** (可选):
```csharp
// 添加到健康检查
app.MapGet("/metrics/db-pool", (IDbConnectionFactory factory) =>
{
    // 返回连接池统计
    return new
    {
        TotalConnections = factory.GetStats().TotalConnections,
        IdleConnections = factory.GetStats().IdleConnections,
        BusyConnections = factory.GetStats().BusyConnections,
        QueuedWaiters = factory.GetStats().QueuedWaiters
    };
});
```

---

### 任务 3.3: 敏感数据掩码 (1天)
**目标**: 从错误响应和日志中移除财务金额

**实现 — 错误响应掩码**:
```csharp
// Post: Extensions/ErrorResponseExtensions.cs
public static class ErrorResponseExtensions
{
    public static ApiErrorResponse MaskSensitiveData(this ApiErrorResponse response)
    {
        var maskedContext = response.Context.ToDictionary(
            x => x.Key,
            x => x.Key.Contains("amount", StringComparison.OrdinalIgnoreCase)
                || x.Key.Contains("total", StringComparison.OrdinalIgnoreCase)
                || x.Key.Contains("price", StringComparison.OrdinalIgnoreCase)
                || x.Key.Contains("rate", StringComparison.OrdinalIgnoreCase)
                    ? (object)"***"
                    : x.Value
        );
        
        return response with { Context = maskedContext };
    }
}

// Program.cs: 应用掩码
catch (PostingException ex)
{
    var response = ApiErrorResponse.FromException(ex, ctx.TraceIdentifier)
        .MaskSensitiveData();  // 🔐 掩码在序列化前
    
    await ctx.Response.WriteAsJsonAsync(response);
}
```

**实现 — 完整日志**:
```csharp
// 仅在后端/服务器日志中保留完整数据
public sealed class PostingCommandHandler : ICommandHandler<PostInvoiceCommand>
{
    public async Task HandleAsync(PostInvoiceCommand command, CancellationToken ct)
    {
        _logger.LogInformation(
            "Processing invoice posting: CompanyId={CompanyId}, InvoiceId={InvoiceId}, Amount={Amount:F2}",
            command.CompanyId,
            command.InvoiceId,
            command.DocumentTotal  // ✅ 完整金额在后端日志中
        );
        
        // ... 处理 ...
        
        // 客户端响应使用掩码
        var errorResponse = new ApiErrorResponse(/*...*/)
            .MaskSensitiveData();
    }
}
```

---

### 任务 3.4: 验证失败审计日志 (1天)
**目的**: 操作员能查询"为什么此发布被拒绝?"

**实现**:
```csharp
// Post: Repositories/IValidationAuditLog.cs
public interface IValidationAuditLog
{
    Task RecordValidationFailureAsync(
        CompanyId companyId,
        UserId userId,
        string documentType,
        Guid sourceId,
        string validationErrorCode,
        string validationErrorMessage,
        CancellationToken ct);
}

// Post: Posting/DefaultPostingValidator.cs (修改)
public sealed class DefaultPostingValidator : IPostingValidator
{
    private readonly IValidationAuditLog _auditLog;
    
    public async Task<ValidationResult> ValidateAsync(PostingDocument document, CancellationToken ct)
    {
        // ... 验证逻辑 ...
        
        if (!isValid)
        {
            // 记录验证失败
            await _auditLog.RecordValidationFailureAsync(
                document.CompanyId,
                _session.UserId,
                document.DocumentType,
                document.SourceId,
                errorCode,
                errorMessage,
                ct
            );
            
            throw new ValidationException(errorCode, errorMessage);
        }
    }
}
```

---

## ✅ Week 4 验收标准

- [ ] 所有 /accounting 端点受中央授权保护
- [ ] 数据库连接池配置正确，无连接耗尽
- [ ] API 错误响应中金额已掩码，服务器日志完整
- [ ] 验证失败可追踪 (审计日志)
- [ ] 所有新代码通过代码审查

---

## 🎯 **交付检查清单** (总体)

### ✅ 代码质量
- [ ] 所有新类都有 > 80% 单元测试覆盖
- [ ] 集成测试包括并发场景
- [ ] Sonarqube 无新代码异味
- [ ] 无运行时警告

### ✅ 安全性
- [ ] OWASP Top 10 审计通过
- [ ] 敏感数据掩码完整
- [ ] RBAC 完全强制
- [ ] SQL 注入风险为零

### ✅ 性能
- [ ] 健康检查响应 <500ms
- [ ] PostPayment 在 <2秒内完成 (包括锁竞争)
- [ ] 连接池无饥荒

### ✅ 可观测性
- [ ] /health endpoint 完全可用且可依赖
- [ ] 所有临界路径都有 correlation IDs
- [ ] Sentry 捕获所有异常 + 分类

### ✅ UI/UX
- [ ] FX 覆盖对话框符合 CitusDesignLanguage
- [ ] 错误消息清晰且可操作
- [ ] 验证错误内联显示
- [ ] 加载状态和离线指标存在

---

## 📅 **时间线总结**

| 周 | 任务 | 人日 | 关键风险 |
|----|------|------|---------|
| **Week 1** | 错误码 + 健康检查 | 4 | 遗漏异常路径 |
| **Week 2-3** | 并发锁 + 熔断器 | 6 | 数据库死锁，缓存不一致 |
| **Week 4** | 授权 + 数据保护 + 超时 | 4 | 覆盖遗漏 |
| **Week 5-6** | 测试 + 审查 + 部署准备 | 5 | |
| **总计** | | **19 人日** | |

**并行度**: 如果有足够的工程师，Weeks 2-3 可以分为 2 个小队 (并发 vs 熔断器)

---

## 🚀 **部署就绪检查清单** (最终)

### Pre-Production 环境
- [ ] 预生产 PostgreSQL 配置与生产相同
- [ ] Redis 集群配置 (>= 3 节点，1 个 sentinel)
- [ ] 监控栈 (Prometheus + Grafana) 完整
- [ ] 日志聚合 (ELK 或 CloudWatch) 工作
- [ ] Sentry 项目创建 + 集成

### 功能验收
- [ ] 单货币工作流全通过 (Invoice → Payment → Settled)
- [ ] 多货币工作流全通过 (GBP ↔ USD ↔ EUR)
- [ ] 并发压力测试: 100 并发支付无重复
- [ ] Frankfurter 宕机测试: 系统继续运作
- [ ] 权限测试: Role 级 RBAC 生效
- [ ] 审计日志: 完全追踪从用户到账目

### 性能基准 (SLA)
- [ ] P95 发布延迟 < 3 秒
- [ ] 连接池 CPU 使用 < 40%
- [ ] 缓存命中率 > 85% (FX)
- [ ] 错误率 < 0.1% (客户端错误除外)

### 运维就绪
- [ ] 监控告警已配置 (+database down, +high error rate, +circuit breaker open)
- [ ] 故障转移流程文档化 (DB fail主从切换)
- [ ] 恢复流程演练 1 次通过
- [ ] 团队培训: 如何解读错误代码 + 如何使用 FX 覆盖

---

## 📚 **文档清单**

需要为每个组件创建:
1. **架构决策记录** (ADR): 为什么选择 Polly? 为什么选择 PostgreSQL 锁?
2. **API 文档**: 所有错误码 + 重试策略
3. **运维手册**: 
   - 如何处理 circuit breaker 打开
   - 如何手动覆盖汇率
   - 如何解读健康检查状态
4. **故障树**: 常见问题 + 根本原因 + 恢复步骤

---

## ✨ **最终交付物**

当所有上述任务完成时，您将拥有:

✅ **可靠的后端系统**
- 并发安全 (无竞态)
- 外部服务弹性 (熔断器 + 缓存)
- 完整的审计跟踪
- 结构化错误码

✅ **生产级监控**
- 健康检查端点
- Sentry 错误追踪
- 连接池监控
- 性能指标

✅ **企业级安全**
- 中央授权
- 敏感数据掩码
- 完整的验证审计
- RBAC 强制

✅ **优雅的用户体验**
- 一致的错误消息
- CI设计系统 UI 组件 (FX 覆盖对话框)
- 在线指标 (连接池、缓存)

🎉 **随时可部署到生产环境**
