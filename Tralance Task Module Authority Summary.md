Gobooks Task Module Authority Summary v4
1. 模块定义

Task 是一个面向客户服务交付的执行单元。

它的核心职责有四件事：

记录为客户做了什么，要收多少钱
记录为客户服务了多少时间，要收多少钱
允许用户从 Task 直接发起与该工作相关的 PO 或 Expense
接收与该 Task 直接相关的 AP 成本，从而计算单个 Task 的 gross margin / contribution margin

Task 的本质是：

confirmed work 进入执行，形成 ready-to-bill 结果，并沉淀该工作单元的收入与直接成本。

2. Feature 开关属性

Task 模块同样属于 company-scoped feature。

可按公司开启或关闭
开关入口放在
Settings → Company → Features
关闭只影响未来入口与未来行为
关闭不会重写历史 Task、AR、AP 关联结果
已存在的 Task、已形成的 draft / invoice、已形成的 PO / Expense / Bill / AP cost linkage，不因关闭而被自动删除、回滚或改写

这意味着：

Task 是可控模块，不是全系统永久强制开放模块。

3. 模块边界
Task 负责
customer-facing 的服务工作记录
fixed-value work
time-based work
billable cost + surplus / markup
从 confirmed work 进入执行
从执行完成进入 ready-to-bill
允许从 Task 发起 PO / Expense
接收 AR 回传的 billed 结果
接收 AP 回传的 direct cost
计算单个 Task 的 gross margin / contribution margin
向 employee / team / department KPI 汇总
Task 不负责
合同管理
Quote / Sales Order 主体管理
AR 收款状态
AP 付款状态
AR aging / AP aging
partially paid / paid / outstanding / collected
通用项目管理
库存执行
制造 / warehouse work order
复杂合同计费体系

一句话：

Task 是执行、发起成本、沉淀单项盈利分析的模块，但不是合同模块，不是 AR 模块，也不是 AP 模块。

4. 与 Quote / Sales Order / AR / AP 的关系
Quote / Sales Order

负责售前承诺与商务确认。

当 Sales Order 或等价确认动作已经成立，并传导到 Task 模块时，Task 即进入：

open
Task

负责售后执行、ready-to-bill 形成，以及与该工作直接相关的成本发起入口。

AR / Invoice

负责开票、应收、收款状态。

Task 只接收 AR 的高层结果：

billed

Task 不承担 AR 的 payment lifecycle。

AP

负责 PO、vendor bill、expense、vendor credit、AP liability、付款及其生命周期。

Task 只接收 AP 的高层结果：

与该 Task 明确关联的 actual direct cost

Task 不承担 AP 的 payment lifecycle。

完整链路

SO / Quote confirmed → Task open → Task 可发起 PO / Expense → AP posted direct cost linked into Task → Task completed → AR / Invoice → Task billed

5. 从 Task 发起 PO / Expense

这是 v4 新增的明确能力。

5.1 允许的动作

用户可以直接从 Task 端发起：

PO
Expense

这意味着 Task 不只是被动接收成本，还可以作为相关成本文档的发起入口。

5.2 发起的目的

从 Task 发起 PO / Expense 的目的，是把该工作相关的采购或费用在创建时就与 Task 建立上下文关系，避免后续人工补链。

5.3 发起时应自动带入的上下文

从 Task 发起 PO / Expense 时，应自动预填或绑定：

task_id
customer context
task description / memo context
需要时的默认 service / work context
5.4 发起不等于接管真相

Task 可以发起 PO / Expense，但：

PO / Expense / Bill / Vendor Credit 的 authoritative lifecycle 仍属于 AP
Task 只是来源入口与关联点
AP 的状态、过账、付款、冲销、reverse 仍由 AP 模块负责
5.5 PO 不是 actual direct cost

这一条必须写死：

PO 本身不是 Task 的正式 direct cost。

PO 代表的是：

采购意图
采购承诺
未来可能发生的成本

真正进入 Task gross margin 的正式成本，优先是：

posted expense
posted AP bill
posted vendor credit / reversal 后的净额
5.6 状态约束

默认规则：

open：允许从 Task 发起 PO / Expense
completed：允许，但应提醒这是 completion 后追加成本
canceled：不允许发起新 PO / Expense
billed：默认不再从 Task 主界面发起新的 PO / Expense；但已存在的 AP 文档后续过账、credit、adjustment 仍可继续回流 Task 成本
6. 计费模型

Task 当前只支持三类值来源，不继续扩张复杂模式。

A. Fixed-value Task

表示：

为客户做了一项工作，要收固定金额

例如安装、咨询、设计、一次性交付。

B. Time-based Task

表示：

为客户服务了多少时间，要收多少钱

例如按小时咨询、实施、培训服务。

C. Billable cost + surplus

表示：

为客户发生的可转嫁成本，以及在此基础上的 surplus / markup

当前到此为止，不扩展为：

milestone billing
retainer
subscription
复杂合同计费
多层 pricing engine
7. 状态模型

Task 状态严格收口为四个：

open
completed
billed
canceled
open

已确认工作进入执行。

来源可以是：

手工创建并确认
或由已确认的 Sales Order / Quote 传导进入
completed

工作已完成，且ready to bill

在 Gobooks 语义中：

completed = execution done + billing-ready

billed

AR 侧已经形成 billing 结果，并回传给 Task。

注意：

billed 是 Task 接收 AR 的高层结果，不等于 Task 承担 AR 全部状态机。

canceled

任务取消，不再进入后续 billing，也不应再发起新的 PO / Expense。

8. Task 的“进、过程、出”

Task 不是一个大而全的闭环模块，而是一个执行漏斗。

进
confirmed work 进入 Task
状态成为 open
过程
记录工作内容或服务时间
累积 billable value
允许从 Task 发起相关 PO / Expense
持续接收与该 Task 直接相关的 AP direct cost
形成单个 Task 的成本与价值视图
出
工作完成后变为 completed
AR 接手形成 invoice / billing
AR 回传高层结果，Task 变为 billed
9. 与 AP 的联动原则

Task 想计算 gross margin，必须与 AP 联动，但只能是受控联动。

9.1 Task 需要的 AP 真相

Task 需要知道：

哪些 PO、expense、vendor bill、vendor credit 与该 Task 相关
这些成本的正式金额是多少
成本是否已形成正式 AP 记录
9.2 AP 只提供 direct cost，不移交整个生命周期

Task 只接收：

actual direct cost

Task 不接收：

供应商是否已付款
AP aging
payment status
pay bill workflow
9.3 成本进入 gross margin 的规则

只有正式形成的 AP 成本才进入 Task 的正式成本真相。

优先口径：

posted AP bill
posted expense
posted vendor credit / reversal

draft 不应进入正式 gross margin。
PO 也不应进入正式 gross margin。

9.4 反向动作必须回流

如果 AP 侧发生：

void
reversal
vendor credit
adjustment

Task 的 direct cost 必须同步更新，否则 gross margin 会失真。

9.5 linkage 必须显式

Task 与 AP 的关系必须依赖明确 linkage，不能依赖 memo / description 的文本猜测。

优先建议：

purchase_order_lines.task_id
expense_lines.task_id
bill_lines.task_id

必要时可以有 header-level：

source_task_id

但authoritative linkage 优先在 line level。

10. 收入、成本、毛利

Task 内部应明确区分三类数值。

收入侧
task_estimated_value
task_completed_value
task_ready_to_bill_value
task_billed_value（如确需引用 AR 结果）
成本侧
task_actual_direct_cost

来源于与 Task 明确关联的 AP 成本真相。

毛利侧
task_gross_margin
task_contribution_margin（如定义需要）

基本理解为：

Task value - Task actual direct cost

11. Gross margin 的两层语义

为了避免混乱，建议区分两层：

Operational gross margin

基于执行完成价值：

task_completed_value / task_ready_to_bill_value - task_actual_direct_cost

用于看“这项工作做完以后大致赚多少”。

Billed gross margin

基于 AR 已形成的 billing 结果：

task_billed_value - task_actual_direct_cost

用于看“正式计费后，这项工作赚多少”。

但无论哪一层，收款与否都不属于 Task 语义。

12. AR 边界再次钉死

Task 不承载以下状态：

partially paid
paid
outstanding
collected amount
AR aging

这些一律属于 AR。

Task 只知道：

是否已经进入 billing
billing value 是多少（如有需要）
不知道也不负责“收回来没有”
13. 经营分析范围

Task 模块的经营分析必须坚持“Task 还是 Task”。

适合放进 Task 的指标
新增了多少 Task
每月完成了多少 Task
每个员工完成了多少 Task
每个 team / department 完成了多少 Task
单个 Task 的 estimated value
单个 Task 的 completed / ready-to-bill value
单个 Task 的 actual direct cost
单个 Task 的 gross margin / contribution margin
单个 Task 发起了多少 PO / Expense
员工 / team / department 维度的 KPI roll-up
不适合放进 Task 的指标
collected amount
outstanding amount
payment aging
receivable collection efficiency
vendor payment efficiency

这些分别属于 AR / AP。

14. 命名原则

为了避免和 AR / AP 撞语义，Task 内部不要轻易使用这些名字作为主字段：

billed_amount
collected_amount
outstanding_amount
po_cost
committed_cost 作为正式成本真相

更稳妥的命名是：

task_estimated_value
task_completed_value
task_ready_to_bill_value
task_actual_direct_cost
task_gross_margin

这样能保持：

Task 的执行价值
AR 的开票真相
AP 的成本真相
PO 的采购意图

四者分层清楚。

15. 关闭后的规则

Task 作为 company-scoped feature，被关闭后应遵循以下规则：

只阻断未来入口与未来新建行为
不重写历史 Task
不重写历史 invoice / AR 回传结果
不重写历史 PO / Expense / Bill / AP 成本 linkage
不自动删除已有 Task
不自动改变已有 Task 状态
不自动回滚 gross margin 历史计算结果

也就是说：

关闭是未来行为边界切换，不是历史数据清洗。

16. 设计原则
Principle 1

Task is Task.
只做执行、价值形成、成本发起、直接成本沉淀，不越界去做合同、收款、付款。

Principle 2

Task is feature-gated per company.
Task 不是全局默认永久开启模块，而是可按公司控制启用/关闭的功能模块。

Principle 3

Confirmed work enters Task; Task exits to AR.
Task 是执行中间层，不是起点，也不是终点。

Principle 4

Task may initiate AP documents, but AP remains authoritative.
Task 可以发起 PO / Expense，但 PO / Expense / Bill / Vendor Credit 的真相仍属于 AP。

Principle 5

Task gross margin requires AP truth.
没有 AP direct cost，Task 不能承担完整毛利分析。

Principle 6

AR / AP lifecycles remain authoritative in their own modules.
Task 只接收高层结果，不重建它们的生命周期。

Principle 7

No expansion into inventory or manufacturing execution.
Task 不向仓库作业、制造工单方向扩张。

17. 最终定义

Task 是一个面向客户服务交付的执行单元，用来记录“为客户做了什么”或“为客户服务了多少时间”，形成可计费结果，并允许用户直接从 Task 发起与该工作相关的 PO 或 Expense。Task 接收 AP 侧与该 Task 明确关联的直接成本，从而计算单个 Task 的 gross margin / contribution margin。它只负责从 confirmed work 进入 open，到执行完成进入 completed（ready to bill），再接收 AR 回传 billed。Task 可以作为 AP 文档的发起入口，但不接管 AP 生命周期；PO、Expense、Bill、Vendor Credit 的权威真相仍属于 AP。Task 模块同样属于 company-scoped feature，可按公司启用或关闭；关闭只影响未来入口与行为，不重写历史 Task、AR、AP 关联结果。它不是合同管理，不是 AR，不是 AP，也不是库存或制造作业模块。billable cost + surplus 可以存在，但不扩展成复杂合同计费系统。Task 的经营分析聚焦于单个 Task 的 value、direct cost、gross margin，以及向 employee / team KPI 的汇总；收款、应收、应付付款等状态仍分别属于 AR / AP。