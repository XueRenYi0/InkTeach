using System.Numerics;
using InkEngine;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

public sealed partial class FullUi
{
    // ---- AI 服务页（A2.1）---------------------------------------------------
    // 设置 → AI 服务 → 管理… 进入；字段用原生 TextInputService，按钮/菜单用 D2D。

    private const float ApiPageH = 430f;
    private const float ApiRowH = 40f;
    private const float ApiRowGap = 12f;
    private const float ApiLabelW = 88f;
    private const float ApiSideW = 168f;
    private const float ApiSideGap = 14f;
    private const float ApiItemH = 32f;

    private const int ApiHitProvider = 400;
    private const int ApiHitBase = 401;
    private const int ApiHitKey = 402;
    private const int ApiHitModel = 403;
    private const int ApiHitFetch = 404;
    private const int ApiHitSave = 405;
    private const int ApiHitCancel = 406;
    private const int ApiHitDelete = 407;
    private const int ApiHitKeyLink = 408;
    private const int ApiHitProviderMenu = 409;
    private const int ApiHitModelMenu = 410;
    private const int ApiHitProviderItemBase = 420;
    private const int ApiHitModelItemBase = 440;
    private const int ApiModelMenuMaxRows = 8;

    private AiApiSettingsSnapshot _apiSnapshot;
    private string _apiSelectedId = "";
    private string _apiBase = "";
    private string _apiKey = "";
    private string _apiModel = "";
    private string _apiStatus = "";
    private bool _apiEditingNew;
    private bool _apiFetching;
    private bool _apiDeleteConfirm;
    private bool _apiProviderMenuOpen;
    private bool _apiModelMenuOpen;
    private int _apiModelScroll;
    private List<string> _apiModels = new();
    private Task<AiApiModelsResult> _apiFetchTask;

    private float MoreApiH() => ApiPageH;

    private float ApiContentTop() => MoreHeaderRect().MaxY + 22f;
    private float ApiRowY(int row) => ApiContentTop() + row * (ApiRowH + ApiRowGap);
    private float ApiFieldLeft() => MoreLeftX() + ApiLabelW;
    private float ApiFieldW() => MathF.Max(160f, MoreContentW() - ApiLabelW - ApiSideW - ApiSideGap);

    private RectF ApiLabelRect(int row) => new()
    {
        MinX = MoreLeftX(), MinY = ApiRowY(row),
        MaxX = MoreLeftX() + ApiLabelW - 8f, MaxY = ApiRowY(row) + ApiRowH,
    };

    private RectF ApiFieldRect(int row) => new()
    {
        MinX = ApiFieldLeft(), MinY = ApiRowY(row),
        MaxX = ApiFieldLeft() + ApiFieldW(), MaxY = ApiRowY(row) + ApiRowH,
    };

    private RectF ApiSideRect(int row) => new()
    {
        MinX = ApiFieldLeft() + ApiFieldW() + ApiSideGap,
        MinY = ApiRowY(row),
        MaxX = ApiFieldLeft() + ApiFieldW() + ApiSideGap + ApiSideW,
        MaxY = ApiRowY(row) + ApiRowH,
    };

    private RectF ApiStatusRect() => new()
    {
        MinX = MoreLeftX(), MinY = ApiRowY(4),
        MaxX = MoreLeftX() + MoreContentW(), MaxY = ApiRowY(4) + 24f,
    };

    private RectF ApiBottomRect(int i)
    {
        float w = 88f, h = 40f, gap = 10f;
        float right = MoreLeftX() + MoreContentW();
        float x = right - (3 - i) * (w + gap);
        float y = MoreRect().MaxY - MorePad - h;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    private int ApiProviderCount => _apiSnapshot?.Providers.Count ?? 0;

    private RectF ApiProviderMenuRect()
    {
        int count = ApiProviderCount + 1;
        float h = count * ApiItemH + 8f;
        float y = ApiFieldRect(0).MaxY + 4f;
        float bottomLimit = ApiBottomRect(0).MinY - 8f;
        if (y + h > bottomLimit) y = MathF.Max(MoreHeaderRect().MaxY + 4f, ApiFieldRect(0).MinY - h - 4f);
        return new RectF
        {
            MinX = ApiFieldRect(0).MinX, MinY = y,
            MaxX = ApiSideRect(0).MaxX, MaxY = y + h,
        };
    }

    private RectF ApiProviderItemRect(int i) => new()
    {
        MinX = ApiProviderMenuRect().MinX + 6f,
        MinY = ApiProviderMenuRect().MinY + 4f + i * ApiItemH,
        MaxX = ApiProviderMenuRect().MaxX - 6f,
        MaxY = ApiProviderMenuRect().MinY + 4f + (i + 1) * ApiItemH,
    };

    private int ApiModelVisibleCount => Math.Min(_apiModels.Count, ApiModelMenuMaxRows);

    private RectF ApiModelMenuRect()
    {
        int count = Math.Max(1, ApiModelVisibleCount);
        float h = count * ApiItemH + 8f;
        float y = ApiFieldRect(3).MaxY + 4f;
        float bottomLimit = ApiBottomRect(0).MinY - 8f;
        if (y + h > bottomLimit)
            y = MathF.Max(MoreHeaderRect().MaxY + 4f, ApiFieldRect(3).MinY - h - 4f);
        return new RectF
        {
            MinX = ApiFieldRect(3).MinX, MinY = y,
            MaxX = ApiFieldRect(3).MaxX, MaxY = y + h,
        };
    }

    private RectF ApiModelItemRect(int i)
    {
        int row = i - _apiModelScroll;
        if (row < 0 || row >= ApiModelVisibleCount) return RectF.Empty;
        var m = ApiModelMenuRect();
        return new RectF
        {
            MinX = m.MinX + 6f, MinY = m.MinY + 4f + row * ApiItemH,
            MaxX = m.MaxX - 6f, MaxY = m.MinY + 4f + (row + 1) * ApiItemH,
        };
    }

    /// <summary>从设置行「AI 服务 → 管理…」进入；服务为空时退回旧独立窗（兼容自检/老版本）。</summary>
    private void OpenApiSettingsPage()
    {
        if (_host?.AiApi == null)
        {
            _apiStatus = "AI 服务组件未接入";
            Invalidate();
            return;
        }

        _moreOpen = true;
        EnsurePageHost();
        SetMorePage(MorePage.Api, push: true);
        _apiSnapshot = _host.AiApi.Load();
        _apiSelectedId = _apiSnapshot?.ActiveId ?? "";
        _apiEditingNew = false;
        _apiProviderMenuOpen = false;
        _apiModelMenuOpen = false;
        _apiModelScroll = 0;
        _apiDeleteConfirm = false;
        _apiStatus = "";
        _apiModels.Clear();
        _apiFetchTask = null;
        _apiFetching = false;
        ApplyApiProvider(_apiSelectedId);
        _moreHover = MoreHitNone;
        _morePress = -1;
        _moreH.To(MoreApiH(), Tokens.MoreOpenMs);
        Invalidate();
    }

    private void ApplyApiProvider(string id)
    {
        var p = _apiSnapshot?.Providers.FirstOrDefault(x => x.Id == id);
        if (p == null && _apiSnapshot != null && _apiSnapshot.Providers.Count > 0)
            p = _apiSnapshot.Providers[0];
        _apiSelectedId = p?.Id ?? "";
        _apiBase = p?.BaseUrl ?? "";
        _apiKey = p?.Key ?? "";
        _apiModel = p?.Model ?? "";
        _apiEditingNew = p == null;
        _apiDeleteConfirm = false;
        _apiModelMenuOpen = false;
        _apiModelScroll = 0;
        _apiModels.Clear();
    }

    private string ApiProviderName()
    {
        if (_apiEditingNew) return "新建自定义";
        var p = _apiSnapshot?.Providers.FirstOrDefault(x => x.Id == _apiSelectedId);
        return p?.Name ?? "选择服务商";
    }

    private AiApiProviderInfo ApiSelectedProvider()
        => _apiSnapshot?.Providers.FirstOrDefault(x => x.Id == _apiSelectedId);

    private int ApiHitAt(float x, float y)
    {
        if (_apiModelMenuOpen)
        {
            int n = ApiModelVisibleCount;
            for (int i = 0; i < n; i++)
                if (ApiModelItemRect(_apiModelScroll + i).Contains(x, y))
                    return ApiHitModelItemBase + i;
            if (ApiModelMenuRect().Contains(x, y)) return ApiHitModelMenu;
        }
        if (_apiProviderMenuOpen)
        {
            int count = ApiProviderCount;
            for (int i = 0; i <= count; i++)
                if (ApiProviderItemRect(i).Contains(x, y)) return ApiHitProviderItemBase + i;
            if (ApiProviderMenuRect().Contains(x, y)) return ApiHitProviderMenu;
        }

        if (ApiFieldRect(0).Contains(x, y)) return ApiHitProvider;
        if (ApiFieldRect(1).Contains(x, y)) return ApiHitBase;
        if (ApiFieldRect(2).Contains(x, y)) return ApiHitKey;
        if (ApiFieldRect(3).Contains(x, y))
            return _apiModels.Count > 0 ? ApiHitModelMenu : ApiHitModel;
        if (ApiSideRect(0).Contains(x, y)) return ApiHitKeyLink;
        if (ApiSideRect(3).Contains(x, y)) return ApiHitFetch;
        for (int i = 0; i < 3; i++)
            if (ApiBottomRect(i).Contains(x, y)) return ApiHitSave + i;
        return MoreHitInside;
    }

    private void ApiPointerMove(float x, float y)
    {
        int hit = ApiHitAt(x, y);
        if (hit != _moreHover) { _moreHover = hit; Invalidate(); }
    }

    private void ApiPointerDown(int hit, float x, float y)
    {
        // 原生 EDIT 是置顶窗口；开下拉前先把输入提交并隐藏，否则它会盖在下拉上面。
        if ((hit == ApiHitProvider || hit == ApiHitModelMenu) && _host?.TextInput?.IsEditing == true)
            _host.TextInput.Commit();

        if (_apiModelMenuOpen)
        {
            int n = ApiModelVisibleCount;
            if (hit >= ApiHitModelItemBase && hit < ApiHitModelItemBase + n)
            {
                SelectApiModel(_apiModelScroll + hit - ApiHitModelItemBase);
                return;
            }
            _apiModelMenuOpen = false;
            Invalidate();
            if (hit == ApiHitModelMenu || hit == ApiHitModel) return;
        }

        if (_apiProviderMenuOpen)
        {
            int count = ApiProviderCount;
            if (hit >= ApiHitProviderItemBase && hit <= ApiHitProviderItemBase + count)
            {
                SelectApiProvider(hit - ApiHitProviderItemBase);
                return;
            }
            _apiProviderMenuOpen = false;
            Invalidate();
            if (hit == ApiHitProvider) { Invalidate(); return; }
            if (hit == ApiHitProviderMenu) return;
        }

        switch (hit)
        {
            case ApiHitProvider:
                _apiProviderMenuOpen = !_apiProviderMenuOpen;
                _apiModelMenuOpen = false;
                Invalidate();
                break;
            case ApiHitModelMenu:
                _apiModelMenuOpen = !_apiModelMenuOpen;
                _apiProviderMenuOpen = false;
                if (_apiModelMenuOpen)
                {
                    // 打开时让当前模型尽量在可视行里。
                    int at = _apiModels.IndexOf(_apiModel);
                    int max = Math.Max(0, _apiModels.Count - ApiModelVisibleCount);
                    _apiModelScroll = Math.Clamp(at < 0 ? 0 : at - ApiModelVisibleCount + 1, 0, max);
                }
                Invalidate();
                break;
            case ApiHitBase:
            case ApiHitKey:
            case ApiHitModel:
                BeginApiField(hit);
                break;
            case ApiHitKeyLink:
                if (!string.IsNullOrEmpty(_apiSelectedId)) _host?.AiApi?.OpenKeyUrl(_apiSelectedId);
                break;
            case ApiHitFetch:
                StartApiFetch();
                break;
            case ApiHitSave:
                SaveApi();
                break;
            case ApiHitCancel:
                _host?.TextInput?.Cancel();
                GoBackMorePage();
                break;
            case ApiHitDelete:
                DeleteApi();
                break;
        }
    }

    private void SelectApiProvider(int index)
    {
        _host?.TextInput?.Commit();
        _apiProviderMenuOpen = false;
        _apiModelMenuOpen = false;
        _apiModels.Clear();
        _apiModelScroll = 0;
        if (index < 0 || index > ApiProviderCount) return;
        if (index == ApiProviderCount)
        {
            _apiSelectedId = "";
            _apiEditingNew = true;
            _apiBase = "";
            _apiKey = "";
            _apiModel = "";
            _apiStatus = "填写服务地址、Key、模型后保存";
        }
        else
        {
            var p = _apiSnapshot.Providers[index];
            _apiSelectedId = p.Id;
            _apiEditingNew = false;
            _apiBase = p.BaseUrl;
            _apiKey = p.Key;
            _apiModel = p.Model;
            _apiDeleteConfirm = false;
            _apiStatus = "";
        }
        Invalidate();
    }

    private void SelectApiModel(int index)
    {
        _host?.TextInput?.Commit();
        _apiModelMenuOpen = false;
        if (index < 0 || index >= _apiModels.Count) return;
        _apiModel = _apiModels[index];
        _apiStatus = "";
        Invalidate();
    }

    private string ApiFieldId(int hit) => hit switch
    {
        ApiHitBase => "api.base",
        ApiHitKey => "api.key",
        ApiHitModel => "api.model",
        _ => "",
    };

    private string ApiFieldValue(int hit) => hit switch
    {
        ApiHitBase => _apiBase,
        ApiHitKey => _apiKey,
        ApiHitModel => _apiModel,
        _ => "",
    };

    private void SetApiFieldValue(int hit, string text)
    {
        switch (hit)
        {
            case ApiHitBase: _apiBase = text; break;
            case ApiHitKey: _apiKey = text; break;
            case ApiHitModel: _apiModel = text; break;
        }
    }

    private int ApiFieldRow(int hit) => hit switch
    {
        ApiHitBase => 1,
        ApiHitKey => 2,
        ApiHitModel => 3,
        _ => 1,
    };

    private int ApiNextField(int hit, bool shift)
    {
        int[] order = { ApiHitBase, ApiHitKey, ApiHitModel };
        int at = Array.IndexOf(order, hit);
        if (at < 0) at = 0;
        at = shift ? (at + order.Length - 1) % order.Length : (at + 1) % order.Length;
        return order[at];
    }

    private void BeginApiField(int hit)
    {
        var input = _host?.TextInput;
        if (input == null)
        {
            _apiStatus = "当前版本不支持页内文字输入";
            Invalidate();
            return;
        }

        string id = ApiFieldId(hit);
        if (string.IsNullOrEmpty(id)) return;
        _apiProviderMenuOpen = false;
        _apiModelMenuOpen = false;
        _apiDeleteConfirm = false;
        _apiStatus = "";

        RectF field = ApiFieldRect(ApiFieldRow(hit));
        var editRect = new RectF
        {
            MinX = field.MinX + 2f, MinY = field.MinY + 2f,
            MaxX = field.MaxX - 2f, MaxY = field.MaxY - 2f,
        };

        input.Begin(new TextInputRequest
        {
            Id = id,
            Rect = editRect,
            InitialText = ApiFieldValue(hit),
            Mode = TextInputMode.SingleLine,
            MaxLength = hit == ApiHitModel ? 256 : 512,
            Password = hit == ApiHitKey,
            Visual = new TextInputVisual
            {
                Background = ApiFieldBg,
                Text = InkCol,
                FontSize = 12.5f,
                Padding = 8f,
                CornerRadius = 6f,
            },
            OnCommit = text =>
            {
                SetApiFieldValue(hit, text.Trim());
                _apiStatus = "";
                Invalidate();
            },
            OnCancel = () => Invalidate(),
            OnTab = shift => BeginApiField(ApiNextField(hit, shift)),
        });
        Invalidate();
    }

    private void StartApiFetch()
    {
        if (_apiFetching || _host?.AiApi == null) return;
        if (string.IsNullOrWhiteSpace(_apiBase) || string.IsNullOrWhiteSpace(_apiKey))
        {
            _apiStatus = "先填服务地址和 API Key";
            Invalidate();
            return;
        }
        _host.TextInput?.Commit();
        _apiFetching = true;
        _apiModelMenuOpen = false;
        _apiStatus = "正在获取模型…";
        _apiFetchTask = _host.AiApi.FetchModelsAsync(_apiBase, _apiKey);
        Invalidate();
    }

    /// <summary>PrepareFrame 调用：异步任务完成后在 UI 线程收口，避免跨线程碰界面状态。</summary>
    private void StepApiFetch()
    {
        if (_apiFetchTask == null) return;
        if (!_apiFetchTask.IsCompleted) return;

        var task = _apiFetchTask;
        _apiFetchTask = null;
        _apiFetching = false;
        try
        {
            var r = task.Result;
            if (r.Ok)
            {
                _apiModels = r.Models ?? new List<string>();
                if (_apiModels.Count > 0)
                {
                    if (string.IsNullOrEmpty(_apiModel) || !_apiModels.Contains(_apiModel))
                        _apiModel = PickApiModel(_apiModels);
                    int at = _apiModels.IndexOf(_apiModel);
                    _apiModelScroll = Math.Clamp(
                        at < 0 ? 0 : at - ApiModelVisibleCount + 1,
                        0, Math.Max(0, _apiModels.Count - ApiModelVisibleCount));
                    _apiModelMenuOpen = true;   // 获取完直接展开，用户能看到全部可选模型
                    _apiStatus = $"已获取 {_apiModels.Count} 个模型，请在下拉里选择";
                }
                else
                {
                    _apiStatus = "已获取 0 个模型";
                }
            }
            else _apiStatus = "获取失败：" + (r.Error ?? "未知错误");
        }
        catch (Exception ex)
        {
            _apiStatus = "获取失败：" + ex.Message;
        }
        Invalidate();
    }

    private static string PickApiModel(List<string> models)
    {
        foreach (var m in models)
        {
            string s = m.ToLowerInvariant();
            if (s.Contains("vl") || s.Contains("vision") || s.Contains("omni")
                || s.Contains("ocr") || s.Contains("4v") || s.Contains("multimodal"))
                return m;
        }
        return models.Count > 0 ? models[0] : "";
    }

    private void SaveApi()
    {
        if (_host?.AiApi == null) { _apiStatus = "当前版本不支持保存"; Invalidate(); return; }
        _host.TextInput?.Commit();
        bool ok = _host.AiApi.Save(new AiApiSaveRequest
        {
            Id = _apiSelectedId,
            IsNew = _apiEditingNew,
            BaseUrl = _apiBase,
            Key = _apiKey,
            Model = _apiModel,
        }, out string error, out string activeId);

        if (!ok) { _apiStatus = error; Invalidate(); return; }
        _apiSelectedId = activeId;
        _apiEditingNew = false;
        _apiDeleteConfirm = false;
        _apiStatus = "已保存，下次识别生效";
        _apiSnapshot = _host.AiApi.Load();
        ApplyApiProvider(_apiSelectedId);
        Invalidate();
    }

    private void DeleteApi()
    {
        if (_host?.AiApi == null) return;
        var p = ApiSelectedProvider();
        if (p == null || p.Builtin) { _apiStatus = "预置服务商不能删除"; Invalidate(); return; }
        if (!_apiDeleteConfirm)
        {
            _apiDeleteConfirm = true;
            _apiStatus = $"再点一次删除「{p.Name}」";
            Invalidate();
            return;
        }
        _apiDeleteConfirm = false;
        bool ok = _host.AiApi.Delete(p.Id, out string error);
        if (!ok) { _apiStatus = error; Invalidate(); return; }
        _apiSnapshot = _host.AiApi.Load();
        _apiSelectedId = _apiSnapshot?.ActiveId ?? "";
        ApplyApiProvider(_apiSelectedId);
        _apiStatus = "已删除";
        Invalidate();
    }

    private Color4 ApiFieldBg => _dark
        ? new Color4(0.15f, 0.16f, 0.18f, 1f)
        : new Color4(1f, 1f, 1f, 0.97f);

    private void DrawApiPage(ID2D1DeviceContext ctx)
    {
        string[] labels = { "服务商", "服务地址", "API Key", "模型" };
        for (int i = 0; i < labels.Length; i++)
            _widgets.TextLeftMiddle(ctx, labels[i], ApiLabelRect(i), 12.5f, Brush(ctx, MutedCol));

        DrawApiField(ctx, ApiFieldRect(0), ApiProviderName(), false, false, "选择服务商");
        DrawApiField(ctx, ApiFieldRect(1), _apiBase, IsApiEditing("api.base"), false, "https://api.example.com/v1");
        DrawApiField(ctx, ApiFieldRect(2), _apiKey, IsApiEditing("api.key"), true, "sk-…");
        DrawApiField(ctx, ApiFieldRect(3), _apiModel, IsApiEditing("api.model"), false, "模型名",
                     _apiModels.Count > 0);

        DrawApiButton(ctx, ApiSideRect(0), "获取 API Key ↗", false, true, ApiHitKeyLink);
        DrawApiButton(ctx, ApiSideRect(3), _apiFetching ? "获取中…" : "获取模型", false, true, ApiHitFetch);

        string status = _apiStatus ?? "";
        Color4 statusCol = status.Contains("失败") || status.Contains("不能") || status.Contains("删除失败")
            ? new Color4(0.85f, 0.22f, 0.22f, 1f)
            : MutedCol;
        if (!string.IsNullOrEmpty(status))
            _widgets.TextLeftMiddle(ctx, status, ApiStatusRect(), 11.5f, Brush(ctx, statusCol));

        DrawApiButton(ctx, ApiBottomRect(0), "保存", true, true, ApiHitSave);
        DrawApiButton(ctx, ApiBottomRect(1), "取消", false, true, ApiHitCancel);
        var p = ApiSelectedProvider();
        DrawApiButton(ctx, ApiBottomRect(2), _apiDeleteConfirm ? "再点删除" : "删除",
                      false, p != null && !p.Builtin, ApiHitDelete);

        if (_apiProviderMenuOpen) DrawApiProviderMenu(ctx);
        if (_apiModelMenuOpen) DrawApiModelMenu(ctx);
    }

    private bool IsApiEditing(string id)
        => _host?.TextInput?.IsEditing == true
           && string.Equals(_host.TextInput.EditingId, id, StringComparison.Ordinal);

    private void DrawApiField(ID2D1DeviceContext ctx, RectF r, string text, bool editing,
                              bool password, string placeholder, bool dropdown = false)
    {
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);
        ctx.FillRoundedRectangle(rr, Brush(ctx, ApiFieldBg));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, editing ? Tokens.Accent : BorderCol), editing ? 1.5f : 1f);
        if (editing) return;

        string show = text ?? "";
        if (password && show.Length > 0) show = new string('•', Math.Min(16, show.Length));
        bool empty = string.IsNullOrEmpty(show);
        _widgets.TextLeftMiddle(ctx, empty ? placeholder : show,
            new RectF
            {
                MinX = r.MinX + 10f, MinY = r.MinY,
                MaxX = r.MaxX - (dropdown ? 30f : 10f), MaxY = r.MaxY,
            },
            12.5f, Brush(ctx, empty ? MutedCol : InkCol));
        if (dropdown)
        {
            float cx = r.MaxX - 17f, cy = (r.MinY + r.MaxY) * 0.5f;
            var ink = Brush(ctx, MutedCol);
            ctx.DrawLine(new Vector2(cx - 4f, cy - 2f), new Vector2(cx, cy + 2f), ink, 1.5f);
            ctx.DrawLine(new Vector2(cx, cy + 2f), new Vector2(cx + 4f, cy - 2f), ink, 1.5f);
        }
    }

    private void DrawApiButton(ID2D1DeviceContext ctx, RectF r, string label,
                               bool primary, bool enabled = true, int hit = -1)
    {
        bool hot = enabled && hit >= 0 && _moreHover == hit;
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);
        if (primary)
            ctx.FillRoundedRectangle(rr, Brush(ctx, enabled ? Tokens.Accent : MutedCol));
        else if (hot)
            ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, primary ? Tokens.Accent : BorderCol), 1f);
        Color4 ink = primary ? Tokens.AccentInk : (enabled ? InkCol : MutedCol);
        _widgets.Text(ctx, label, r, 12.5f, Brush(ctx, ink));
    }

    private void DrawApiProviderMenu(ID2D1DeviceContext ctx)
    {
        var m = ApiProviderMenuRect();
        var rr = new RoundedRectangle(new Vortice.RawRectF(m.MinX, m.MinY, m.MaxX, m.MaxY), 10f, 10f);
        ctx.FillRoundedRectangle(rr, Brush(ctx, _dark
            ? new Color4(0.18f, 0.19f, 0.21f, 1f)
            : new Color4(1f, 1f, 1f, 1f)));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        int count = ApiProviderCount;
        for (int i = 0; i <= count; i++)
        {
            var item = ApiProviderItemRect(i);
            bool hot = _moreHover == ApiHitProviderItemBase + i;
            if (hot)
                ctx.FillRoundedRectangle(
                    new RoundedRectangle(new Vortice.RawRectF(item.MinX, item.MinY, item.MaxX, item.MaxY), 6f, 6f),
                    Brush(ctx, HoverCol));
            string text = i < count ? _apiSnapshot.Providers[i].Name : "＋ 新建自定义…";
            bool active = i < count && _apiSnapshot.Providers[i].Id == _apiSelectedId;
            _widgets.TextLeftMiddle(ctx, text,
                new RectF { MinX = item.MinX + 10f, MinY = item.MinY, MaxX = item.MaxX - 10f, MaxY = item.MaxY },
                12.5f, Brush(ctx, active ? Tokens.Accent : InkCol));
        }
    }

    private void DrawApiModelMenu(ID2D1DeviceContext ctx)
    {
        var m = ApiModelMenuRect();
        var rr = new RoundedRectangle(new Vortice.RawRectF(m.MinX, m.MinY, m.MaxX, m.MaxY), 10f, 10f);
        ctx.FillRoundedRectangle(rr, Brush(ctx, _dark
            ? new Color4(0.18f, 0.19f, 0.21f, 1f)
            : new Color4(1f, 1f, 1f, 1f)));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        int n = ApiModelVisibleCount;
        for (int row = 0; row < n; row++)
        {
            int index = _apiModelScroll + row;
            var item = ApiModelItemRect(index);
            bool hot = _moreHover == ApiHitModelItemBase + row;
            if (hot)
                ctx.FillRoundedRectangle(
                    new RoundedRectangle(new Vortice.RawRectF(item.MinX, item.MinY, item.MaxX, item.MaxY), 6f, 6f),
                    Brush(ctx, HoverCol));
            bool active = index < _apiModels.Count && _apiModels[index] == _apiModel;
            _widgets.TextLeftMiddle(ctx, _apiModels[index],
                new RectF { MinX = item.MinX + 10f, MinY = item.MinY, MaxX = item.MaxX - 18f, MaxY = item.MaxY },
                12.5f, Brush(ctx, active ? Tokens.Accent : InkCol));
            if (active)
            {
                float cx = item.MaxX - 14f, cy = (item.MinY + item.MaxY) * 0.5f;
                var ink = Brush(ctx, Tokens.Accent);
                ctx.DrawLine(new Vector2(cx - 4f, cy), new Vector2(cx - 1f, cy + 3f), ink, 1.6f);
                ctx.DrawLine(new Vector2(cx - 1f, cy + 3f), new Vector2(cx + 5f, cy - 4f), ink, 1.6f);
            }
        }

        // 模型很多时右侧给一条细滚动提示（滚轮可滚）。
        int max = Math.Max(0, _apiModels.Count - n);
        if (max > 0)
        {
            float trackX = m.MaxX - 5f, top = m.MinY + 6f, bottom = m.MaxY - 6f;
            float trackH = MathF.Max(8f, bottom - top);
            float thumbH = MathF.Max(18f, trackH * n / _apiModels.Count);
            float t = _apiModelScroll / (float)max;
            float y = top + (trackH - thumbH) * t;
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(trackX, top, trackX + 2f, top + trackH), 1f, 1f),
                Brush(ctx, new Color4(BorderCol.R, BorderCol.G, BorderCol.B, 0.35f)));
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(trackX, y, trackX + 2f, y + thumbH), 1f, 1f),
                Brush(ctx, new Color4(Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, 0.65f)));
        }
    }

    // ---- A2.1 自检钩子 -------------------------------------------------------
    internal void OpenApiPageForTest() { OpenApiSettingsPage(); _moreH.Jump(MoreApiH()); _more.Jump(1f); }

    internal void OpenLogPageForTest()
    {
        _moreOpen = true;
        EnsurePageHost();
        SetMorePage(MorePage.Home, push: false);
        SetMorePage(MorePage.Log, push: true);
        _moreH.Jump(MoreLogH());
        _more.Jump(1f);
    }

    internal int ApiHitAtForTest(float x, float y) => ApiHitAt(x, y);
    internal RectF ApiFieldRectForTest(int row) => ApiFieldRect(row);
    internal RectF ApiBottomRectForTest(int i) => ApiBottomRect(i);
    internal string ApiProviderNameForTest => ApiProviderName();
    internal int ApiProviderCountForTest => ApiProviderCount;
    internal RectF ApiProviderItemRectForTest(int i) => ApiProviderItemRect(i);
    internal string ApiSelectedProviderIdForTest => _apiSelectedId;
    internal int ApiModelCountForTest => _apiModels.Count;
    internal bool ApiModelMenuOpenForTest => _apiModelMenuOpen;
    internal bool ApiProviderMenuOpenForTest => _apiProviderMenuOpen;
    internal bool ApiInputEditingForTest => _host?.TextInput?.IsEditing == true;
    internal string ApiModelForTest => _apiModel;
    internal RectF ApiModelItemRectForTest(int i) => ApiModelItemRect(i);
    internal string ApiStatusForTest => _apiStatus;
    internal void ApiClickForTest(int hit) => ApiPointerDown(hit, 0f, 0f);
}
