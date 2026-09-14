# 公告推送 · 部署与控制台使用（Cloudflare 免费）

一次部署，之后只在控制台发消息，**不需要更新应用**。

## 一、一次性部署（约 3 分钟）

### 方式 A：网页操作（不用装工具）

1. 打开 Cloudflare Dashboard → **Workers & Pages → KV → Create namespace**，名字填 `blt_announce`。
2. → **Create → Worker**（名字随意，例如 `blt-announce`）→ **Edit code**，把 `announce-worker.mjs` 的内容整段粘贴进去 → **Deploy**。
3. 在该 Worker 的 **Settings → Variables and Secrets**：
   - 添加 **KV Namespace Binding**：变量名 `ANN` → 选 `blt_announce`；
   - 添加 **Secret**：名字 `ADMIN_TOKEN`，值填一段你自己生成的长随机串（32 位以上，例如用密码管理器生成）。**这个就是控制台登录密钥，别弄丢**。
4. （可选但推荐）**Settings → Triggers → Custom Domains** 绑定 `announce.ai-daynews.xyz`（需要域名已托管在 Cloudflare）。不绑也能用，用 `https://blt-announce.<你的子域>.workers.dev` 即可。
5. 浏览器打开 `https://<你的域名>/admin` → 粘贴 `ADMIN_TOKEN` → 登录。

### 方式 B：命令行（可选）

```toml
# wrangler.toml
name = "blt-announce"
main = "announce-worker.mjs"
compatibility_date = "2025-01-01"
[[kv_namespaces]]
binding = "ANN"
id = "<创建命名空间后拿到的 id>"
```

```bash
wrangler kv namespace create blt_announce      # 记下返回的 id
wrangler secret put ADMIN_TOKEN                # 粘贴你的长随机串
wrangler deploy
```

## 二、客户端指向这个 Worker

应用默认地址是 `https://announce.ai-daynews.xyz/announcements`。若你用的是 `workers.dev` 默认域，改 `data/config.json`：

```json
{
  "announceUrl": "https://blt-announce.<你的子域>.workers.dev/announcements",
  "announceFallbackUrl": "",
  "announceEnabled": true
}
```

`announceFallbackUrl` 可选（主地址失败时的第二个源，例如 GitHub raw 上的同一份 JSON）。

## 三、日常发公告（控制台）

打开 `https://<你的域名>/admin`：

1. **＋ 新建公告** → 选类型：
   - **普通通知** → 出现在应用**右下角卡片**，用户点「知道了」后不再出现；
   - **持续通知** → 常驻在**顶部横幅**，直到过期或你下架；
   - **强通知** → **全屏模态**，必须点「我已阅读并确认」（`soft` 可「稍后」，下次启动再提醒；`hard` 不确认不能用）。
2. 填标题、正文；可加 1 个按钮（「立即更新」直接调应用内更新，或「打开链接」）。
3. 需要时设置：级别（info/warn/critical）、定时发布、过期时间（到点自动下架 = 撤回）、通道（maui/electron）、版本区间、**指定 UID**（留空=所有用户）。
4. 点 **发布**。客户端 **5 分钟内**看到（打开「紧急模式」后 **60 秒内**）。

其它控制台功能：**保存草稿**、**从线上删除此条**、**发布历史 + 一键恢复上一版**（防误操作）、**回执统计**（每条显示"已读 / 已确认"人数）、**公告开关**（一键关闭所有公告）。

## 四、接口速查

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/announcements?uid=&v=&ch=maui` | 客户端拉取（服务端已按时间/版本/通道/UID 过滤，带 ETag） |
| POST | `/receipt` | 客户端已读/确认回执（只存 uid 哈希与计数） |
| GET | `/admin` | 控制台页面 |
| GET | `/admin/api/state` | 管理：线上内容 / 草稿 / 历史 / 统计 |
| POST | `/admin/api/publish` `/draft` `/rollback` `/cfg` | 发布 / 草稿 / 回滚 / 全局设置 |

## 五、本地自测（不部署也能验）

把应用 `data/config.json` 的 `announceUrl` 临时指向本地文件（放进 `wwwroot/` 即可，Kestrel 会伺服）：

```json
{ "announceUrl": "http://127.0.0.1:7460/announce-sample.json" }
```

`announce-sample.json` 形如：

```json
{ "schema": 1, "serverTime": 0, "pollAfterSeconds": 300,
  "items": [ { "id": "t1", "type": "sticky", "level": "info", "title": "测试", "body": "内容" } ] }
```

## 六、Worker 逻辑自测

```bash
node cloudflare/test-local.mjs     # 33 项断言：鉴权/过滤/ETag/回执去重/紧急模式/回滚/控制台页面
```

## 七、注意事项

- `ADMIN_TOKEN` 只在 Worker 的 Secret 里与你的浏览器本地，别写进代码或公告内容。
- 公开接口只返回"当前该看到的"条目，草稿/历史/统计不会外泄。
- 正文按纯文本渲染，按钮链接只接受 `https://`。
- 「紧急模式」记得用完关掉（它把客户端轮询从 5 分钟改成 60 秒）。
- 免费额度：KV 读 10 万/天、写 1000/天；正常使用远低于此（ETag 让大多数请求是 304）。
