local base = 'https://ilikecomix.com'

function Register()
    module.Name = 'ILikeComix'
    module.Domains.Add('ilikecomix.com')
end

function GetInfo()
    info.Title = dom.SelectValue('//h1') or dom.SelectValue('//meta[@property="og:title"]/@content')
    info.Description = dom.SelectValue('//div[contains(@class,"description-summary")]//div[contains(@class,"summary__content")]')
    info.Artist = dom.SelectValues('//div[contains(@class,"artist-content")]//a')
end

function GetChapters()
    if url:match('/manga/c/[^/]+/[^/?#]+') then return end
    for link in dom.SelectElements('//li[contains(concat(" ",normalize-space(@class)," ")," wp-manga-chapter ")]/a') do
        local chapter = ChapterInfo.New()
        chapter.Url = link.SelectValue('@href')
        chapter.Title = link.SelectValue('.'):trim()
        chapter.Chapter = chapter.Title:match('^(%d+%.?%d*)%s*%.')
        chapters.Add(chapter)
    end
end

function GetPages()
    pages.Referer = base .. '/'
    for img in dom.SelectElements('//img[contains(concat(" ",normalize-space(@class)," ")," wp-manga-chapter-img ")]') do
        local src = img.SelectValue('@data-src') or img.SelectValue('@src')
        if src and src ~= '' then pages.Add(src:trim()) end
    end
end
