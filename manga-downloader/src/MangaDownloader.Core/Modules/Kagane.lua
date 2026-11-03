local base = 'https://kagane.moe'

function Register()
    module.Name = 'Kagane'
    module.Domains.Add('kagane.moe')
    module.DeferHttpRequests = true
end

local function ids()
    local series = url:match('/series/([^/?#]+)')
    local book = url:match('/reader/([^/?#]+)')
    local function uuid(value)
        return value and value:match('(%x%x%x%x%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%x%x%x%x%x%x%x%x)')
    end
    series, book = uuid(series), uuid(book)
    if not series then Fail('Expected a Kagane series or reader URL containing a series ID') end
    return series, book
end

local function getJson(client, address)
    local response = client.GetResponse(address)
    if response.StatusCode ~= 200 then Fail('Kagane returned HTTP ' .. response.StatusCode .. ' for ' .. address) end
    return Json.New(response.Body)
end

function GetInfo()
    local series = ids()
    local data = getJson(http, base .. '/api/v2/series/' .. series)
    info.Title = data.SelectValue('title')
    info.Description = data.SelectValue('description')
    info.Language = data.SelectValue('translated_language')
    info.Status = data.SelectValue('publication_status')
    info.Type = data.SelectValue('format')
    info.Id = series
end

function GetChapters()
    local series, book = ids()
    if book then return end
    local address = base .. '/api/v2/series/' .. series .. '/books'
    local seen = {}
    repeat
        local data = getJson(http, address)
        for item in data.SelectTokens('items[*]') do
            local chapter = ChapterInfo.New()
            chapter.Url = base .. '/series/' .. series .. '/reader/' .. item.SelectValue('book_id')
            chapter.Title = item.SelectValue('title')
            chapter.Chapter = item.SelectValue('chapter_no')
            chapter.Volume = item.SelectValue('volume_no')
            chapters.Add(chapter)
        end
        local cursor = data.SelectValue('next_cursor')
        if cursor and cursor ~= '' then
            if seen[cursor] then Fail('Kagane returned a repeated chapter cursor') end
            seen[cursor] = true
            cursor = cursor:gsub('([^%w%-_%.~])', function(c) return string.format('%%%02X', string.byte(c)) end)
            address = base .. '/api/v2/series/' .. series .. '/books?before=' .. cursor
        else
            address = nil
        end
    until not address
end

function GetPages()
    local series, book = ids()
    if not book then Fail('Expected a Kagane chapter reader URL') end
    local client = http.New()
    client.Headers['Content-Type'] = 'application/json'
    local response = client.PostResponse(base .. '/api/integrity', '{}')
    if response.StatusCode ~= 200 then Fail('Kagane integrity request failed: HTTP ' .. response.StatusCode) end
    local token = Json.New(response.Body).SelectValue('token')
    if not token then Fail('Kagane did not return an integrity token') end
    client.Headers['x-integrity-token'] = token
    response = client.PostResponse(base .. '/api/v2/books/' .. book .. '?intent=Read', '')
    if response.StatusCode ~= 200 then Fail('Kagane chapter request failed: HTTP ' .. response.StatusCode) end
    local data = Json.New(response.Body)
    local cache = data.SelectValue('cache_url')
    local access = data.SelectValue('access_token')
    if not cache or not access then Fail('Kagane did not return a chapter access token') end
    pages.Referer = base .. '/'
    pages.Headers['Origin'] = base
    for item in data.SelectTokens('manifest.pages[*]') do
        local ext = item.SelectValue('ext')
        local kind = ext == 'jxl' and 'page/original' or 'page'
        pages.Add(cache .. '/api/v2/books/' .. kind .. '/' .. book .. '/' .. item.SelectValue('page_id') .. '.' .. ext .. '?token=' .. access)
    end
end
