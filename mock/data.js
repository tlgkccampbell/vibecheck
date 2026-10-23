// Hand-written changeset data for the viewer mock.
//
// This stands in for the chunked changeset document that `archmap diff` will emit.
// The fictional system is "Orchard", an order platform. The fictional changeset is
// PR #412, "Move payment retry into Billing core, add refunds, emit payment events".
//
// NODES describes every node once. CHUNKS holds one entry per container with the
// lifted edges among its children, the ports that connect children to nodes outside
// the container, and the stored layout. LEAVES holds the file list and hunks per leaf.

const NODES = {
  gateway: { name: "Gateway", purpose: "HTTP edge, routing and request middleware.", policy: "declared" },
  "gateway.http": { name: "HTTP", purpose: "Kestrel host and endpoint mapping.", leaf: true, files: 6 },
  "gateway.routing": { name: "Routing", purpose: "Maps routes to component APIs.", leaf: true, files: 4 },
  "gateway.middleware": { name: "Middleware", purpose: "Session validation and request logging.", leaf: true, files: 5 },

  orders: { name: "Orders", purpose: "Order lifecycle from cart to fulfillment.", policy: "declared" },
  "orders.api": { name: "API", purpose: "Order endpoints.", leaf: true, files: 5 },
  "orders.core": { name: "Core", purpose: "Order state machine and validation.", leaf: true, files: 11 },
  "orders.store": { name: "Store", purpose: "Order persistence.", leaf: true, files: 4 },

  billing: { name: "Billing", purpose: "Owns invoices, payments and the ledger.", policy: "declared" },
  "billing.api": { name: "API", purpose: "Billing endpoints.", leaf: true, files: 4 },
  "billing.core": { name: "Core", purpose: "Invoicing, payment orchestration and pricing.", policy: "declared" },
  "billing.core.invoices": { name: "Invoices", purpose: "Invoice generation and numbering.", leaf: true, files: 7 },
  "billing.core.pricing": { name: "Pricing", purpose: "Tax and discount calculation.", leaf: true, files: 5 },
  "billing.core.self": { name: "self", purpose: "Files directly in Core.", leaf: true, files: 2 },
  "billing.core.payments": { name: "Payments", purpose: "Capture, refund and retry of card payments.", policy: "declared" },
  "billing.core.payments.capture": { name: "Capture", purpose: "Captures an authorized payment.", leaf: true, files: 3, changed: 1 },
  "billing.core.payments.refunds": { name: "Refunds", purpose: "Issues full and partial refunds.", leaf: true, files: 2, changed: 2, status: "added" },
  "billing.core.payments.self": { name: "self", purpose: "Files directly in Payments.", leaf: true, files: 2, changed: 1 },
  "billing.core.payments.retry": { name: "Retry", purpose: "Retry policy and backoff for failed charges.", policy: "declared", status: "added" },
  "billing.core.payments.retry.policy": { name: "Policy", purpose: "Decides whether a failure is retried.", leaf: true, files: 1, changed: 1, status: "added" },
  "billing.core.payments.retry.backoff": { name: "Backoff", purpose: "Exponential backoff schedule.", leaf: true, files: 1, changed: 1, status: "added" },
  "billing.ledger": { name: "Ledger", purpose: "Double-entry ledger and postings.", leaf: true, files: 9 },
  "billing.integrations": { name: "Integrations", purpose: "Adapters to payment providers.", policy: "observed" },
  "billing.integrations.stripe": { name: "Stripe", purpose: "Stripe client and webhook handling.", policy: "observed" },
  "billing.integrations.stripe.client": { name: "Client", purpose: "Typed wrapper over the Stripe SDK.", leaf: true, files: 3, changed: 1 },
  "billing.integrations.stripe.retry": { name: "Retry", purpose: "Retry wrapper around Stripe calls.", leaf: true, files: 1, changed: 1, status: "removed" },
  "billing.integrations.webhooks": { name: "Webhooks", purpose: "Inbound provider webhooks.", leaf: true, files: 2 },

  inventory: { name: "Inventory", purpose: "Stock levels and reservations.", policy: "observed" },
  "inventory.reservations": { name: "Reservations", purpose: "Holds stock for pending orders.", leaf: true, files: 4, changed: 1 },
  "inventory.stock": { name: "Stock", purpose: "Stock ledger per warehouse.", leaf: true, files: 6 },
  "inventory.reporting": { name: "Reporting", purpose: "Stock reports and low-stock alerts.", leaf: true, files: 3, changed: 1 },

  auth: { name: "Auth", purpose: "Identity, sessions and authorization decisions.", policy: "declared" },
  "auth.sessions": { name: "Sessions", purpose: "Session lookup and scope checks.", leaf: true, files: 3, changed: 1 },
  "auth.tokens": { name: "Tokens", purpose: "Token issuance and rotation.", leaf: true, files: 4 },
  "auth.policies": { name: "Policies", purpose: "Authorization policy evaluation.", leaf: true, files: 5 },

  notifications: { name: "Notifications", purpose: "Outbound email, SMS and push.", policy: "declared" },
  "notifications.dispatch": { name: "Dispatch", purpose: "Routes events to channels.", leaf: true, files: 3, changed: 1 },
  "notifications.channels": { name: "Channels", purpose: "Email, SMS and push senders.", leaf: true, files: 5 },
  "notifications.templates": { name: "Templates", purpose: "Message templates per event.", leaf: true, files: 6, changed: 1 },

  shared: { name: "Shared", purpose: "Primitives with no business meaning.", policy: "declared" },
  "shared.kernel": { name: "Kernel", purpose: "Result types, ids, clock.", leaf: true, files: 12 },
  "shared.messaging": { name: "Messaging", purpose: "In-process event bus.", leaf: true, files: 4 },

  stripe: { name: "Stripe SDK", purpose: "External assembly: Stripe, Stripe.*", external: true },
};

// Lifted edges among the children of each container, and ports to the outside.
// An edge has base and head evidence counts. Status is derived from them unless set.
const CHUNKS = {
  root: {
    layout: {
      gateway: [40, 300, 170, 110],
      orders: [320, 40, 210, 120],
      billing: [600, 290, 220, 150],
      inventory: [320, 540, 210, 120],
      auth: [900, 60, 200, 110],
      notifications: [900, 300, 200, 120],
      stripe: [900, 540, 180, 90],
      shared: [40, 700, 1100, 56],
    },
    edges: [
      { from: "gateway", to: "orders", label: "Routes order requests", base: 14, head: 14 },
      { from: "gateway", to: "billing", label: "Routes billing requests", base: 6, head: 6 },
      { from: "gateway", to: "inventory", base: 3, head: 3 },
      { from: "gateway", to: "auth", label: "Validates sessions", base: 5, head: 5 },
      { from: "orders", to: "billing", payload: "OrderPlaced", base: 9, head: 9 },
      { from: "orders", to: "inventory", payload: "Reservation", base: 7, head: 7 },
      { from: "orders", to: "notifications", payload: "OrderEvent", base: 4, head: 4 },
      { from: "billing", to: "auth", label: "Authorizes payment operations", base: 8, head: 11 },
      { from: "billing", to: "stripe", label: "Charges cards", payload: "ChargeRequest", base: 12, head: 9 },
      { from: "billing", to: "notifications", payload: "PaymentEvent", base: 0, head: 3 },
      { from: "gateway", to: "shared", base: 10, head: 10 },
      { from: "orders", to: "shared", base: 31, head: 31 },
      { from: "billing", to: "shared", base: 44, head: 47 },
      { from: "inventory", to: "shared", base: 18, head: 18 },
      { from: "auth", to: "shared", base: 12, head: 12 },
      { from: "notifications", to: "shared", base: 15, head: 16 },
    ],
    ports: [],
  },

  gateway: {
    layout: { "gateway.http": [60, 120, 200, 110], "gateway.routing": [420, 120, 200, 110], "gateway.middleware": [240, 360, 200, 110] },
    edges: [
      { from: "gateway.http", to: "gateway.routing", base: 9, head: 9 },
      { from: "gateway.http", to: "gateway.middleware", base: 4, head: 4 },
    ],
    ports: [
      { child: "gateway.routing", outside: "orders", dir: "out", base: 14, head: 14 },
      { child: "gateway.routing", outside: "billing", dir: "out", base: 6, head: 6 },
      { child: "gateway.routing", outside: "inventory", dir: "out", base: 3, head: 3 },
      { child: "gateway.middleware", outside: "auth", dir: "out", base: 5, head: 5 },
      { child: "gateway.http", outside: "shared", dir: "out", base: 10, head: 10 },
    ],
  },

  orders: {
    layout: { "orders.api": [60, 180, 200, 110], "orders.core": [400, 180, 220, 130], "orders.store": [760, 180, 200, 110] },
    edges: [
      { from: "orders.api", to: "orders.core", base: 12, head: 12 },
      { from: "orders.core", to: "orders.store", base: 8, head: 8 },
    ],
    ports: [
      { child: "orders.api", outside: "gateway", dir: "in", base: 14, head: 14 },
      { child: "orders.core", outside: "billing", dir: "out", payload: "OrderPlaced", base: 9, head: 9 },
      { child: "orders.core", outside: "inventory", dir: "out", payload: "Reservation", base: 7, head: 7 },
      { child: "orders.core", outside: "notifications", dir: "out", payload: "OrderEvent", base: 4, head: 4 },
      { child: "orders.core", outside: "shared", dir: "out", base: 31, head: 31 },
    ],
  },

  billing: {
    layout: {
      "billing.api": [60, 200, 200, 110],
      "billing.core": [400, 160, 260, 190],
      "billing.ledger": [400, 480, 260, 110],
      "billing.integrations": [800, 160, 240, 190],
    },
    edges: [
      { from: "billing.api", to: "billing.core", base: 7, head: 7 },
      { from: "billing.core", to: "billing.ledger", base: 15, head: 15 },
      { from: "billing.core", to: "billing.integrations", label: "Charges via adapter", base: 12, head: 8 },
    ],
    ports: [
      { child: "billing.api", outside: "gateway", dir: "in", base: 6, head: 6 },
      { child: "billing.core", outside: "orders", dir: "in", payload: "OrderPlaced", base: 9, head: 9 },
      { child: "billing.core", outside: "auth", dir: "out", base: 8, head: 11 },
      { child: "billing.core", outside: "notifications", dir: "out", payload: "PaymentEvent", base: 0, head: 3 },
      { child: "billing.integrations", outside: "stripe", dir: "out", payload: "ChargeRequest", base: 12, head: 9 },
      { child: "billing.core", outside: "shared", dir: "out", base: 30, head: 33 },
      { child: "billing.ledger", outside: "shared", dir: "out", base: 14, head: 14 },
    ],
  },

  "billing.core": {
    layout: {
      "billing.core.invoices": [60, 120, 200, 110],
      "billing.core.payments": [400, 100, 260, 190],
      "billing.core.pricing": [60, 400, 200, 110],
      "billing.core.self": [800, 120, 180, 90],
    },
    edges: [
      { from: "billing.core.invoices", to: "billing.core.pricing", base: 6, head: 6 },
      { from: "billing.core.payments", to: "billing.core.invoices", label: "Marks invoices paid", base: 4, head: 5 },
      { from: "billing.core.self", to: "billing.core.payments", base: 2, head: 2 },
      { from: "billing.core.self", to: "billing.core.invoices", base: 2, head: 2 },
    ],
    ports: [
      { child: "billing.core.self", outside: "billing.api", dir: "in", base: 7, head: 7 },
      { child: "billing.core.self", outside: "orders", dir: "in", payload: "OrderPlaced", base: 9, head: 9 },
      { child: "billing.core.payments", outside: "auth", dir: "out", base: 8, head: 11 },
      { child: "billing.core.payments", outside: "notifications", dir: "out", payload: "PaymentEvent", base: 0, head: 3 },
      { child: "billing.core.payments", outside: "billing.integrations", dir: "out", base: 12, head: 8 },
      { child: "billing.core.invoices", outside: "billing.ledger", dir: "out", base: 15, head: 15 },
      { child: "billing.core.invoices", outside: "shared", dir: "out", base: 18, head: 18 },
      { child: "billing.core.payments", outside: "shared", dir: "out", base: 12, head: 15 },
    ],
  },

  "billing.core.payments": {
    layout: {
      "billing.core.payments.self": [60, 200, 180, 90],
      "billing.core.payments.capture": [380, 100, 220, 110],
      "billing.core.payments.refunds": [380, 340, 220, 110],
      "billing.core.payments.retry": [780, 200, 240, 150],
    },
    edges: [
      { from: "billing.core.payments.self", to: "billing.core.payments.capture", base: 3, head: 3 },
      { from: "billing.core.payments.self", to: "billing.core.payments.refunds", base: 0, head: 2 },
      { from: "billing.core.payments.capture", to: "billing.core.payments.retry", label: "Wraps charge attempts", base: 0, head: 4 },
      { from: "billing.core.payments.refunds", to: "billing.core.payments.retry", base: 0, head: 2 },
    ],
    ports: [
      { child: "billing.core.payments.self", outside: "billing.core.self", dir: "in", base: 2, head: 2 },
      { child: "billing.core.payments.capture", outside: "auth", dir: "out", base: 8, head: 8 },
      { child: "billing.core.payments.refunds", outside: "auth", dir: "out", base: 0, head: 3 },
      { child: "billing.core.payments.capture", outside: "notifications", dir: "out", payload: "PaymentEvent", base: 0, head: 2 },
      { child: "billing.core.payments.refunds", outside: "notifications", dir: "out", payload: "PaymentEvent", base: 0, head: 1 },
      { child: "billing.core.payments.capture", outside: "billing.integrations", dir: "out", base: 12, head: 5 },
      { child: "billing.core.payments.refunds", outside: "billing.integrations", dir: "out", base: 0, head: 3 },
      { child: "billing.core.payments.capture", outside: "billing.core.invoices", dir: "out", base: 4, head: 4 },
      { child: "billing.core.payments.refunds", outside: "billing.core.invoices", dir: "out", base: 0, head: 1 },
      { child: "billing.core.payments.capture", outside: "shared", dir: "out", base: 12, head: 12 },
      { child: "billing.core.payments.retry", outside: "shared", dir: "out", base: 0, head: 3 },
    ],
  },

  "billing.core.payments.retry": {
    layout: { "billing.core.payments.retry.policy": [120, 200, 220, 110], "billing.core.payments.retry.backoff": [600, 200, 220, 110] },
    edges: [
      { from: "billing.core.payments.retry.policy", to: "billing.core.payments.retry.backoff", label: "Schedules next attempt", base: 0, head: 2 },
    ],
    ports: [
      { child: "billing.core.payments.retry.policy", outside: "billing.core.payments.capture", dir: "in", base: 0, head: 4 },
      { child: "billing.core.payments.retry.policy", outside: "billing.core.payments.refunds", dir: "in", base: 0, head: 2 },
      { child: "billing.core.payments.retry.backoff", outside: "shared", dir: "out", base: 0, head: 3 },
    ],
  },

  "billing.integrations": {
    layout: { "billing.integrations.stripe": [160, 160, 260, 180], "billing.integrations.webhooks": [680, 200, 220, 110] },
    edges: [
      { from: "billing.integrations.webhooks", to: "billing.integrations.stripe", base: 3, head: 3 },
    ],
    ports: [
      { child: "billing.integrations.stripe", outside: "billing.core", dir: "in", base: 12, head: 8 },
      { child: "billing.integrations.stripe", outside: "stripe", dir: "out", payload: "ChargeRequest", base: 12, head: 9 },
    ],
  },

  "billing.integrations.stripe": {
    layout: { "billing.integrations.stripe.client": [160, 200, 220, 110], "billing.integrations.stripe.retry": [640, 200, 220, 110] },
    edges: [
      { from: "billing.integrations.stripe.client", to: "billing.integrations.stripe.retry", base: 3, head: 0 },
    ],
    ports: [
      { child: "billing.integrations.stripe.client", outside: "billing.core", dir: "in", base: 12, head: 8 },
      { child: "billing.integrations.stripe.client", outside: "stripe", dir: "out", payload: "ChargeRequest", base: 9, head: 9 },
      { child: "billing.integrations.stripe.retry", outside: "stripe", dir: "out", base: 3, head: 0 },
    ],
  },

  inventory: {
    layout: { "inventory.reservations": [60, 120, 220, 110], "inventory.stock": [460, 120, 220, 110], "inventory.reporting": [460, 400, 220, 110] },
    edges: [
      { from: "inventory.reservations", to: "inventory.stock", base: 11, head: 11 },
      { from: "inventory.reporting", to: "inventory.stock", base: 6, head: 6 },
      { from: "inventory.reservations", to: "inventory.reporting", base: 0, head: 2 },
    ],
    ports: [
      { child: "inventory.reservations", outside: "gateway", dir: "in", base: 3, head: 3 },
      { child: "inventory.reservations", outside: "orders", dir: "in", payload: "Reservation", base: 7, head: 7 },
      { child: "inventory.stock", outside: "shared", dir: "out", base: 18, head: 18 },
    ],
  },

  auth: {
    layout: { "auth.sessions": [60, 160, 220, 110], "auth.tokens": [460, 60, 220, 110], "auth.policies": [460, 320, 220, 110] },
    edges: [
      { from: "auth.sessions", to: "auth.tokens", base: 5, head: 5 },
      { from: "auth.sessions", to: "auth.policies", base: 4, head: 6 },
    ],
    ports: [
      { child: "auth.sessions", outside: "gateway", dir: "in", base: 5, head: 5 },
      { child: "auth.sessions", outside: "billing", dir: "in", base: 8, head: 11 },
      { child: "auth.tokens", outside: "shared", dir: "out", base: 12, head: 12 },
    ],
  },

  notifications: {
    layout: { "notifications.dispatch": [60, 160, 220, 110], "notifications.channels": [460, 60, 220, 110], "notifications.templates": [460, 320, 220, 110] },
    edges: [
      { from: "notifications.dispatch", to: "notifications.channels", base: 7, head: 7 },
      { from: "notifications.dispatch", to: "notifications.templates", base: 5, head: 7 },
    ],
    ports: [
      { child: "notifications.dispatch", outside: "orders", dir: "in", payload: "OrderEvent", base: 4, head: 4 },
      { child: "notifications.dispatch", outside: "billing", dir: "in", payload: "PaymentEvent", base: 0, head: 3 },
      { child: "notifications.channels", outside: "shared", dir: "out", base: 15, head: 16 },
    ],
  },

  shared: {
    layout: { "shared.kernel": [160, 200, 220, 110], "shared.messaging": [640, 200, 220, 110] },
    edges: [
      { from: "shared.messaging", to: "shared.kernel", base: 6, head: 6 },
    ],
    ports: [
      { child: "shared.kernel", outside: "gateway", dir: "in", base: 10, head: 10 },
      { child: "shared.kernel", outside: "orders", dir: "in", base: 24, head: 24 },
      { child: "shared.messaging", outside: "orders", dir: "in", base: 7, head: 7 },
      { child: "shared.kernel", outside: "billing", dir: "in", base: 40, head: 43 },
      { child: "shared.messaging", outside: "billing", dir: "in", base: 4, head: 4 },
      { child: "shared.kernel", outside: "inventory", dir: "in", base: 18, head: 18 },
      { child: "shared.kernel", outside: "auth", dir: "in", base: 12, head: 12 },
      { child: "shared.kernel", outside: "notifications", dir: "in", base: 9, head: 9 },
      { child: "shared.messaging", outside: "notifications", dir: "in", base: 6, head: 7 },
    ],
  },
};

// Structural map changes in the changeset, with the evidence that justifies each.
const STRUCTURAL = [
  { kind: "edge", op: "added", at: "root", from: "billing", to: "notifications", payload: "PaymentEvent",
    evidence: ["CapturePayment.cs:41 calls Notifications.Dispatcher.Publish", "CapturePayment.cs:19 references PaymentEvent", "RefundPayment.cs:33 calls Notifications.Dispatcher.Publish"] },
  { kind: "node", op: "added", id: "billing.core.payments.retry", evidence: ["binds 2 files at head"] },
  { kind: "node", op: "added", id: "billing.core.payments.retry.policy", evidence: ["binds RetryPolicy.cs"] },
  { kind: "node", op: "added", id: "billing.core.payments.retry.backoff", evidence: ["binds ExponentialBackoff.cs"] },
  { kind: "node", op: "added", id: "billing.core.payments.refunds", evidence: ["binds RefundPayment.cs, RefundRequest.cs"] },
  { kind: "node", op: "removed", id: "billing.integrations.stripe.retry", evidence: ["bound no file at head"] },
  { kind: "edge", op: "added", at: "billing.core.payments", from: "billing.core.payments.capture", to: "billing.core.payments.retry", label: "Wraps charge attempts",
    evidence: ["CapturePayment.cs:28 constructs RetryPolicy", "CapturePayment.cs:31 calls RetryPolicy.Execute"] },
  { kind: "edge", op: "added", at: "billing.core.payments", from: "billing.core.payments.refunds", to: "billing.core.payments.retry",
    evidence: ["RefundPayment.cs:24 calls RetryPolicy.Execute"] },
  { kind: "edge", op: "added", at: "billing.core.payments", from: "billing.core.payments.self", to: "billing.core.payments.refunds",
    evidence: ["PaymentService.cs:52 calls RefundPayment.Issue"] },
  { kind: "edge", op: "added", at: "billing.core.payments.retry", from: "billing.core.payments.retry.policy", to: "billing.core.payments.retry.backoff", label: "Schedules next attempt",
    evidence: ["RetryPolicy.cs:21 calls ExponentialBackoff.Delay"] },
];

// File lists and hunks per leaf. A hunk is a list of [marker, text] lines where the
// marker is " " for context, "-" for a removed line and "+" for an added line.
const LEAVES = {
  "billing.core.payments.capture": {
    files: [
      { path: "src/Orchard.Billing/Core/Payments/Capture/CapturePayment.cs", status: "modified", hunks: [
        { header: "@@ -1,12 +1,16 @@", lines: [
          [" ", "using Orchard.Billing.Integrations.Stripe;"],
          ["-", "using Orchard.Billing.Integrations.Stripe.Retry;"],
          ["+", "using Orchard.Billing.Core.Payments.Retry;"],
          ["+", "using Orchard.Notifications.Dispatch;"],
          [" ", "using Orchard.Shared.Kernel;"],
          [" ", ""],
          [" ", "namespace Orchard.Billing.Core.Payments.Capture;"],
          [" ", ""],
          [" ", "public sealed class CapturePayment"],
          [" ", "{"],
          [" ", "    private readonly StripeClient _stripe;"],
          ["-", "    private readonly StripeRetry _retry;"],
          ["+", "    private readonly RetryPolicy _retry;"],
          ["+", "    private readonly Dispatcher _dispatcher;"],
          [" ", "    private readonly IClock _clock;"],
        ] },
        { header: "@@ -24,10 +28,18 @@", lines: [
          [" ", "    public async Task<Result<CaptureReceipt>> ExecuteAsync(CaptureRequest request, CancellationToken ct)"],
          [" ", "    {"],
          ["-", "        var charge = await _retry.WrapAsync(() => _stripe.CaptureAsync(request.ToCharge(), ct));"],
          ["+", "        var charge = await _retry.ExecuteAsync("],
          ["+", "            () => _stripe.CaptureAsync(request.ToCharge(), ct),"],
          ["+", "            RetryClassifier.ForCapture,"],
          ["+", "            ct);"],
          [" ", "        if (charge.IsFailure)"],
          [" ", "        {"],
          [" ", "            return charge.Error;"],
          [" ", "        }"],
          [" ", ""],
          ["+", "        await _dispatcher.PublishAsync("],
          ["+", "            new PaymentEvent(PaymentEventKind.Captured, request.OrderId, charge.Value.Amount, _clock.UtcNow),"],
          ["+", "            ct);"],
          ["+", ""],
          [" ", "        return new CaptureReceipt(charge.Value.Id, charge.Value.Amount, _clock.UtcNow);"],
          [" ", "    }"],
        ] },
      ] },
      { path: "src/Orchard.Billing/Core/Payments/Capture/CaptureRequest.cs", status: "unchanged" },
      { path: "src/Orchard.Billing/Core/Payments/Capture/CaptureReceipt.cs", status: "unchanged" },
    ],
  },
  "billing.core.payments.refunds": {
    files: [
      { path: "src/Orchard.Billing/Core/Payments/Refunds/RefundPayment.cs", status: "added", hunks: [
        { header: "@@ -0,0 +1,38 @@", lines: [
          ["+", "using Orchard.Auth.Sessions;"],
          ["+", "using Orchard.Billing.Core.Payments.Retry;"],
          ["+", "using Orchard.Billing.Integrations.Stripe;"],
          ["+", "using Orchard.Notifications.Dispatch;"],
          ["+", "using Orchard.Shared.Kernel;"],
          ["+", ""],
          ["+", "namespace Orchard.Billing.Core.Payments.Refunds;"],
          ["+", ""],
          ["+", "public sealed class RefundPayment"],
          ["+", "{"],
          ["+", "    private readonly StripeClient _stripe;"],
          ["+", "    private readonly RetryPolicy _retry;"],
          ["+", "    private readonly Dispatcher _dispatcher;"],
          ["+", "    private readonly ISessionReader _sessions;"],
          ["+", ""],
          ["+", "    public async Task<Result<RefundReceipt>> IssueAsync(RefundRequest request, CancellationToken ct)"],
          ["+", "    {"],
          ["+", "        var session = await _sessions.RequireScopeAsync(request.SessionId, Scope.RefundPayments, ct);"],
          ["+", "        if (session.IsFailure)"],
          ["+", "        {"],
          ["+", "            return session.Error;"],
          ["+", "        }"],
          ["+", ""],
          ["+", "        var refund = await _retry.ExecuteAsync("],
          ["+", "            () => _stripe.RefundAsync(request.ChargeId, request.Amount, ct),"],
          ["+", "            RetryClassifier.ForRefund,"],
          ["+", "            ct);"],
          ["+", "        if (refund.IsFailure)"],
          ["+", "        {"],
          ["+", "            return refund.Error;"],
          ["+", "        }"],
          ["+", ""],
          ["+", "        await _dispatcher.PublishAsync("],
          ["+", "            new PaymentEvent(PaymentEventKind.Refunded, request.OrderId, request.Amount, refund.Value.At),"],
          ["+", "            ct);"],
          ["+", "        return new RefundReceipt(refund.Value.Id, request.Amount, refund.Value.At);"],
          ["+", "    }"],
          ["+", "}"],
        ] },
      ] },
      { path: "src/Orchard.Billing/Core/Payments/Refunds/RefundRequest.cs", status: "added", hunks: [
        { header: "@@ -0,0 +1,5 @@", lines: [
          ["+", "namespace Orchard.Billing.Core.Payments.Refunds;"],
          ["+", ""],
          ["+", "public sealed record RefundRequest(SessionId SessionId, OrderId OrderId, ChargeId ChargeId, Money Amount);"],
          ["+", ""],
          ["+", "public sealed record RefundReceipt(RefundId Id, Money Amount, DateTimeOffset At);"],
        ] },
      ] },
    ],
  },
  "billing.core.payments.self": {
    files: [
      { path: "src/Orchard.Billing/Core/Payments/PaymentService.cs", status: "modified", hunks: [
        { header: "@@ -45,6 +45,14 @@", lines: [
          [" ", "    public Task<Result<CaptureReceipt>> CaptureAsync(CaptureRequest request, CancellationToken ct)"],
          [" ", "        => _capture.ExecuteAsync(request, ct);"],
          [" ", ""],
          ["+", "    public Task<Result<RefundReceipt>> RefundAsync(RefundRequest request, CancellationToken ct)"],
          ["+", "    {"],
          ["+", "        ArgumentNullException.ThrowIfNull(request);"],
          ["+", "        return _refunds.IssueAsync(request, ct);"],
          ["+", "    }"],
          ["+", ""],
          [" ", "    public Task<Result<Invoice>> InvoiceAsync(OrderId orderId, CancellationToken ct)"],
          [" ", "        => _invoices.GenerateAsync(orderId, ct);"],
        ] },
      ] },
      { path: "src/Orchard.Billing/Core/Payments/PaymentEvent.cs", status: "unchanged" },
    ],
  },
  "billing.core.payments.retry.policy": {
    files: [
      { path: "src/Orchard.Billing/Core/Payments/Retry/RetryPolicy.cs", status: "added", hunks: [
        { header: "@@ -0,0 +1,34 @@", lines: [
          ["+", "using Orchard.Shared.Kernel;"],
          ["+", ""],
          ["+", "namespace Orchard.Billing.Core.Payments.Retry;"],
          ["+", ""],
          ["+", "/// <summary>Retries a payment operation according to a classifier and a backoff schedule.</summary>"],
          ["+", "public sealed class RetryPolicy"],
          ["+", "{"],
          ["+", "    private readonly ExponentialBackoff _backoff;"],
          ["+", "    private readonly int _maxAttempts;"],
          ["+", ""],
          ["+", "    public RetryPolicy(ExponentialBackoff backoff, int maxAttempts = 4)"],
          ["+", "    {"],
          ["+", "        _backoff = backoff;"],
          ["+", "        _maxAttempts = maxAttempts;"],
          ["+", "    }"],
          ["+", ""],
          ["+", "    public async Task<Result<T>> ExecuteAsync<T>(Func<Task<Result<T>>> operation, Func<Error, bool> isTransient, CancellationToken ct)"],
          ["+", "    {"],
          ["+", "        for (var attempt = 1; ; attempt++)"],
          ["+", "        {"],
          ["+", "            var result = await operation();"],
          ["+", "            if (result.IsSuccess || attempt == _maxAttempts || !isTransient(result.Error))"],
          ["+", "            {"],
          ["+", "                return result;"],
          ["+", "            }"],
          ["+", ""],
          ["+", "            await Task.Delay(_backoff.Delay(attempt), ct);"],
          ["+", "        }"],
          ["+", "    }"],
          ["+", "}"],
        ] },
      ] },
    ],
  },
  "billing.core.payments.retry.backoff": {
    files: [
      { path: "src/Orchard.Billing/Core/Payments/Retry/ExponentialBackoff.cs", status: "added", hunks: [
        { header: "@@ -0,0 +1,16 @@", lines: [
          ["+", "namespace Orchard.Billing.Core.Payments.Retry;"],
          ["+", ""],
          ["+", "/// <summary>Computes the delay before a retry attempt, doubling each time up to a ceiling.</summary>"],
          ["+", "public sealed class ExponentialBackoff"],
          ["+", "{"],
          ["+", "    private readonly TimeSpan _initial;"],
          ["+", "    private readonly TimeSpan _ceiling;"],
          ["+", ""],
          ["+", "    public ExponentialBackoff(TimeSpan initial, TimeSpan ceiling)"],
          ["+", "    {"],
          ["+", "        _initial = initial;"],
          ["+", "        _ceiling = ceiling;"],
          ["+", "    }"],
          ["+", ""],
          ["+", "    public TimeSpan Delay(int attempt)"],
          ["+", "        => TimeSpan.FromTicks(Math.Min(_initial.Ticks << (attempt - 1), _ceiling.Ticks));"],
        ] },
      ] },
    ],
  },
  "billing.integrations.stripe.client": {
    files: [
      { path: "src/Orchard.Billing/Integrations/Stripe/StripeClient.cs", status: "modified", hunks: [
        { header: "@@ -8,14 +8,10 @@", lines: [
          [" ", "public sealed class StripeClient"],
          [" ", "{"],
          [" ", "    private readonly global::Stripe.StripeClient _inner;"],
          ["-", "    private readonly StripeRetry _retry;"],
          [" ", ""],
          ["-", "    public StripeClient(global::Stripe.StripeClient inner, StripeRetry retry)"],
          ["+", "    public StripeClient(global::Stripe.StripeClient inner)"],
          [" ", "    {"],
          [" ", "        _inner = inner;"],
          ["-", "        _retry = retry;"],
          [" ", "    }"],
          [" ", ""],
          [" ", "    public async Task<Result<Charge>> CaptureAsync(ChargeRequest request, CancellationToken ct)"],
          ["-", "        => await _retry.WrapAsync(() => CaptureOnceAsync(request, ct));"],
          ["+", "        => await CaptureOnceAsync(request, ct);"],
        ] },
      ] },
      { path: "src/Orchard.Billing/Integrations/Stripe/ChargeMapping.cs", status: "unchanged" },
      { path: "src/Orchard.Billing/Integrations/Stripe/StripeOptions.cs", status: "unchanged" },
    ],
  },
  "billing.integrations.stripe.retry": {
    files: [
      { path: "src/Orchard.Billing/Integrations/Stripe/Retry/StripeRetry.cs", status: "removed", hunks: [
        { header: "@@ -1,22 +0,0 @@", lines: [
          ["-", "namespace Orchard.Billing.Integrations.Stripe.Retry;"],
          ["-", ""],
          ["-", "public sealed class StripeRetry"],
          ["-", "{"],
          ["-", "    private static readonly TimeSpan[] Delays = { TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4) };"],
          ["-", ""],
          ["-", "    public async Task<Result<T>> WrapAsync<T>(Func<Task<Result<T>>> operation)"],
          ["-", "    {"],
          ["-", "        Result<T> last = default;"],
          ["-", "        foreach (var delay in Delays)"],
          ["-", "        {"],
          ["-", "            last = await operation();"],
          ["-", "            if (last.IsSuccess || !last.Error.IsTransient)"],
          ["-", "            {"],
          ["-", "                return last;"],
          ["-", "            }"],
          ["-", "            await Task.Delay(delay);"],
          ["-", "        }"],
          ["-", "        return last;"],
          ["-", "    }"],
          ["-", "}"],
        ] },
      ] },
    ],
  },
  "auth.sessions": {
    files: [
      { path: "src/Orchard.Auth/Sessions/ISessionReader.cs", status: "modified", hunks: [
        { header: "@@ -6,4 +6,7 @@", lines: [
          [" ", "public interface ISessionReader"],
          [" ", "{"],
          [" ", "    Task<Result<Session>> RequireAsync(SessionId id, CancellationToken ct);"],
          ["+", ""],
          ["+", "    /// <summary>Returns the session when it exists and carries the scope, and an error otherwise.</summary>"],
          ["+", "    Task<Result<Session>> RequireScopeAsync(SessionId id, Scope scope, CancellationToken ct);"],
          [" ", "}"],
        ] },
      ] },
      { path: "src/Orchard.Auth/Sessions/Session.cs", status: "unchanged" },
      { path: "src/Orchard.Auth/Sessions/SessionReader.cs", status: "unchanged" },
    ],
  },
  "notifications.dispatch": {
    files: [
      { path: "src/Orchard.Notifications/Dispatch/Dispatcher.cs", status: "modified", hunks: [
        { header: "@@ -30,6 +30,7 @@", lines: [
          [" ", "        return evt switch"],
          [" ", "        {"],
          [" ", "            OrderEvent o => _templates.ForOrder(o),"],
          ["+", "            PaymentEvent p => _templates.ForPayment(p),"],
          [" ", "            _ => throw new NotSupportedException(evt.GetType().Name),"],
          [" ", "        };"],
          [" ", "    }"],
        ] },
      ] },
      { path: "src/Orchard.Notifications/Dispatch/IEvent.cs", status: "unchanged" },
      { path: "src/Orchard.Notifications/Dispatch/Subscription.cs", status: "unchanged" },
    ],
  },
  "notifications.templates": {
    files: [
      { path: "src/Orchard.Notifications/Templates/PaymentTemplates.cs", status: "added", hunks: [
        { header: "@@ -0,0 +1,12 @@", lines: [
          ["+", "namespace Orchard.Notifications.Templates;"],
          ["+", ""],
          ["+", "public static class PaymentTemplates"],
          ["+", "{"],
          ["+", "    public static Message ForPayment(PaymentEvent evt) => evt.Kind switch"],
          ["+", "    {"],
          ["+", "        PaymentEventKind.Captured => new Message(\"Payment received\", $\"We received {evt.Amount} for order {evt.OrderId}.\"),"],
          ["+", "        PaymentEventKind.Refunded => new Message(\"Refund issued\", $\"We refunded {evt.Amount} for order {evt.OrderId}.\"),"],
          ["+", "        _ => throw new ArgumentOutOfRangeException(nameof(evt)),"],
          ["+", "    };"],
          ["+", "}"],
        ] },
      ] },
      { path: "src/Orchard.Notifications/Templates/OrderTemplates.cs", status: "unchanged" },
      { path: "src/Orchard.Notifications/Templates/Message.cs", status: "unchanged" },
    ],
  },
  "inventory.reservations": {
    files: [
      { path: "src/Orchard.Inventory/Reservations/ReservationService.cs", status: "modified", hunks: [
        { header: "@@ -3,6 +3,7 @@", lines: [
          [" ", "using Orchard.Inventory.Stock;"],
          ["+", "using Orchard.Inventory.Reporting;"],
          [" ", "using Orchard.Shared.Kernel;"],
        ] },
        { header: "@@ -41,6 +42,10 @@", lines: [
          [" ", "        var held = await _stock.HoldAsync(line.Sku, line.Quantity, ct);"],
          [" ", "        if (held.IsFailure)"],
          [" ", "        {"],
          ["+", "            // Nudge the low-stock report so the alert fires before the next scheduled run."],
          ["+", "            await _report.RefreshAsync(line.Sku, ct);"],
          ["+", ""],
          [" ", "            return held.Error;"],
          [" ", "        }"],
        ] },
      ] },
      { path: "src/Orchard.Inventory/Reservations/Reservation.cs", status: "unchanged" },
      { path: "src/Orchard.Inventory/Reservations/ReservationStore.cs", status: "unchanged" },
    ],
  },
  "inventory.reporting": {
    files: [
      { path: "src/Orchard.Inventory/Reporting/StockReport.cs", status: "modified", hunks: [
        { header: "@@ -18,4 +18,10 @@", lines: [
          [" ", "    public Task<Report> BuildAsync(CancellationToken ct) => _builder.BuildAsync(ct);"],
          ["+", ""],
          ["+", "    public async Task RefreshAsync(Sku sku, CancellationToken ct)"],
          ["+", "    {"],
          ["+", "        var level = await _stock.LevelAsync(sku, ct);"],
          ["+", "        _alerts.Evaluate(sku, level);"],
          ["+", "    }"],
          [" ", "}"],
        ] },
      ] },
      { path: "src/Orchard.Inventory/Reporting/LowStockAlerts.cs", status: "unchanged" },
    ],
  },
};
