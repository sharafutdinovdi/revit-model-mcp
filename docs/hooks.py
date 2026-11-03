"""Resolve included Markdown links relative to their repository source."""

import html
import json
import posixpath
import re
from pathlib import Path
from urllib.parse import urlsplit

import tomllib
from bs4 import BeautifulSoup
from markdown import Markdown

SOURCES = {
    "server.md": "server/README.md",
    "contributing.md": "CONTRIBUTING.md",
    "changelog.md": "CHANGELOG.md",
    "security-policy.md": "SECURITY.md",
}


def on_page_markdown(markdown, page, config, files):
    root = Path(config.config_file_path).parent
    source = SOURCES.get(page.file.src_uri, f"docs/{page.file.src_uri}")
    # Expand snippets before MkDocs validates links, retaining one source of content.
    processor = Markdown(
        extensions=["pymdownx.snippets"],
        extension_configs={
            "pymdownx.snippets": {"base_path": [str(root)], "check_paths": True}
        },
    )
    markdown = "\n".join(processor.preprocessors["snippet"].run(markdown.splitlines()))
    pages = {value: key for key, value in SOURCES.items()}
    if page.file.src_uri in SOURCES:
        page.edit_url = f"{config.repo_url}/edit/main/{source}"

    def resolve(target):
        if target.startswith(config.site_url):
            relative = target[len(config.site_url) :]
            path, separator, anchor = relative.partition("#")
            candidate = f"{path.rstrip('/')}.md" if path else "index.md"
            if files.get_file_from_path(candidate):
                return candidate + (separator + anchor if separator else "")
        url = urlsplit(target)
        if url.scheme or url.netloc or not url.path:
            return target
        path = posixpath.normpath(posixpath.join(posixpath.dirname(source), url.path))
        suffix = (f"?{url.query}" if url.query else "") + (
            f"#{url.fragment}" if url.fragment else ""
        )
        if path in pages:
            return pages[path] + suffix
        if path.startswith("docs/"):
            return (
                posixpath.relpath(path[5:], posixpath.dirname(page.file.src_uri))
                + suffix
            )
        if (root / path).is_file():
            return f"{config.repo_url}/blob/main/{path}{suffix}"
        return target

    markdown = re.sub(
        r"(\]\()([^\s)]+)(\))", lambda m: m[1] + resolve(m[2]) + m[3], markdown
    )
    markdown = re.sub(
        r'(\b(?:src|srcset)=")([^"]+)(")',
        lambda m: m[1] + resolve(m[2]) + m[3],
        markdown,
    )
    return markdown


def on_post_page(output, page, config):
    """Publish crawlable metadata without changing the theme or visible content."""
    title = f"{page.title} - {config.site_name}"
    description = page.meta["description"]
    image = f"{config.site_url}assets/social-card.png"
    tags = {
        "og:type": "website",
        "og:title": title,
        "og:description": description,
        "og:image": image,
        "og:url": page.canonical_url,
        "twitter:card": "summary_large_image",
        "twitter:title": title,
        "twitter:description": description,
        "twitter:image": image,
    }
    metadata = "\n".join(
        f'<meta {"property" if key.startswith("og:") else "name"}="{key}" '
        f'content="{html.escape(value, quote=True)}">'
        for key, value in tags.items()
    )
    schema = None
    if page.file.src_uri == "index.md":
        root = Path(config.config_file_path).parent
        package = tomllib.loads((root / "server/pyproject.toml").read_text())
        schema = {
            "@context": "https://schema.org",
            "@type": "SoftwareApplication",
            "name": "Model MCP",
            "description": description,
            "url": page.canonical_url,
            "applicationCategory": "DesignApplication",
            "operatingSystem": "Windows",
            "offers": {"@type": "Offer", "price": "0", "priceCurrency": "USD"},
            "license": "https://opensource.org/license/mit/",
            "softwareVersion": package["project"]["version"],
            "downloadUrl": f"{config.repo_url}/releases/latest",
            "codeRepository": config.repo_url,
        }
    elif page.file.src_uri == "guides/faq.md":
        content = BeautifulSoup(page.content, "html.parser")
        questions = []
        for heading in content.find_all("h2"):
            anchor = heading.find(class_="headerlink")
            if anchor:
                anchor.decompose()
            question = heading.get_text(strip=True)
            if question.endswith("?"):
                answer = heading.find_next_sibling("p")
                questions.append(
                    {
                        "@type": "Question",
                        "name": question,
                        "acceptedAnswer": {
                            "@type": "Answer",
                            "text": answer.get_text(" ", strip=True),
                        },
                    }
                )
        schema = {
            "@context": "https://schema.org",
            "@type": "FAQPage",
            "mainEntity": questions,
        }
    if schema:
        # Escape HTML delimiters in JSON embedded in a script element.
        data = json.dumps(schema).replace("<", "\\u003c")
        metadata += f'\n<script type="application/ld+json">{data}</script>'
    return output.replace("</head>", f"{metadata}\n</head>", 1)
