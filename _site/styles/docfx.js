// Copyright (c) Microsoft. All rights reserved. Licensed under the MIT license. See LICENSE file in the project root for full license information.
$(function () {
  var active = 'active';
  var expanded = 'in';
  var collapsed = 'collapsed';
  var filtered = 'filtered';
  var show = 'show';
  var hide = 'hide';

  // Line highlight for code snippet
  (function () {
    $('pre code[highlight-lines]').each(function(i, block) {
      if (block.innerHTML === "") return;
      var lines = block.innerHTML.split('\n');

      queryString = block.getAttribute('highlight-lines');
      if (!queryString) return;

      var ranges = queryString.split(',');
      for (var j = 0, range; range = ranges[j++];) {
        var found = range.match(/^(\d+)\-(\d+)?$/);
        if (found) {
          // consider region as `{startlinenumber}-{endlinenumber}`, in which {endlinenumber} is optional
          var start = +found[1];
          var end = +found[2];
          if (isNaN(end) || end > lines.length) {
            end = lines.length;
          }
        } else {
          // consider region as a sigine line number
          if (isNaN(range)) continue;
          var start = +range;
          var end = start;
        }
        if (start <= 0 || end <= 0 || start > end || start > lines.length) {
          // skip current region if invalid
          continue;
        }
        lines[start - 1] = '<span class="line-highlight">' + lines[start - 1];
        lines[end - 1] = lines[end - 1] + '</span>';
      }

      block.innerHTML = lines.join('\n');
    });
  })();

  //Adjust the position of search box in navbar
  (function () {
    autoCollapse();
    $(window).on('resize', autoCollapse);
    $(document).on('click', '.navbar-collapse.in', function (e) {
      if ($(e.target).is('a')) {
        $(this).collapse('hide');
      }
    });

    function autoCollapse() {
      var navbar = $('#autocollapse');
      if (navbar.height() === null) {
        setTimeout(autoCollapse, 300);
      }
      navbar.removeClass(collapsed);
      if (navbar.height() > 60) {
        navbar.addClass(collapsed);
      }
    }
  })();

  // Support full-text-search
  (function () {
    var query;
    var relHref = $("meta[property='docfx\\:rel']").attr("content");
    if (relHref) {
      var search = searchFactory();
      search();
      highlightKeywords();
      addSearchEvent();
    }

    // Search factory
    function searchFactory() {
      var worker = new Worker(relHref + 'styles/search-worker.js');
      if (!worker) return localSearch;
      return window.Worker ? webWorkerSearch : localSearch;

      function localSearch() {
        console.log("using local search");
        var lunrIndex = lunr(function () {
          this.ref('href');
          this.field('title', { boost: 50 });
          this.field('keywords', { boost: 20 });
        });
        lunr.tokenizer.seperator = /[\s\-\.]+/;
        var searchData = {};
        var searchDataRequest = new XMLHttpRequest();

        var indexPath = relHref + "index.json";
        if (indexPath) {
          searchDataRequest.open('GET', indexPath);
          searchDataRequest.onload = function () {
            searchData = JSON.parse(this.responseText);
            for (var prop in searchData) {
              lunrIndex.add(searchData[prop]);
            }
          }
          searchDataRequest.send();
        }

        $("body").bind("queryReady", function () {
          var hits = lunrIndex.search(query);
          var results = [];
          hits.forEach(function (hit) {
            var item = searchData[hit.ref];
            results.push({ 'href': item.href, 'title': item.title, 'keywords': item.keywords });
          });
          handleSearchResults(results);
        });
      }

      function webWorkerSearch() {
        console.log("using Web Worker");
        var indexReady = $.Deferred();

        worker.onmessage = function (oEvent) {
          switch (oEvent.data.e) {
            case 'index-ready':
              indexReady.resolve();
              break;
            case 'query-ready':
              var hits = oEvent.data.d;
              handleSearchResults(hits);
              break;
          }
        }

        indexReady.promise().done(function () {
          $("body").bind("queryReady", function () {
            worker.postMessage({ q: query });
          });
        });
      }
    };

    // Highlight the searching keywords
    function highlightKeywords() {
      var q = url('?q');
      if (q !== null) {
        var keywords = q.split("%20");
        keywords.forEach(function (keyword) {
          if (keyword !== "") {
            highlight($('.data-searchable *'), keyword, "<mark>");
            highlight($('article *'), keyword, "<mark>");
          }
        });
      }
    }

    function addSearchEvent() {
      $('body').bind("searchEvent", function () {
        $('#search-query').keypress(function (e) {
          return e.which !== 13;
        });

        $('#search-query').keyup(function () {
          query = $(this).val();
          if (query.length < 3) {
            flipContents("show");
          } else {
            flipContents("hide");
            $("body").trigger("queryReady");
            $('#search-results>.search-list').text('Search Results for "' + query + '"');
          }
        }).off("keydown");
      });
    }

    function flipContents(action) {
      if (action === "show") {
        $('.hide-when-search').show();
        $('#search-results').hide();
      } else {
        $('.hide-when-search').hide();
        $('#search-results').show();
      }
    }

    function highlight(nodes, rgxStr, tag) {
      var rgx = new RegExp(rgxStr, "gi");
      nodes.each(function () {
        $(this).contents().filter(function () {
          return this.nodeType == 3 && rgx.test(this.nodeValue);
        }).replaceWith(function () {
          return (this.nodeValue || "").replace(rgx, function (match) {
            return $(tag).text(match)[0].outerHTML;
          });
        });
      });
    }

    function relativeUrlToAbsoluteUrl(currentUrl, relativeUrl) {
      var currentItems = currentUrl.split(/\/+/);
      var relativeItems = relativeUrl.split(/\/+/);
      var depth = currentItems.length - 1;
      var items = [];
      for (var i = 0; i < relativeItems.length; i++) {
        if (relativeItems[i] === '..') {
          depth--;
        } else if (relativeItems[i] !== '.') {
          items.push(relativeItems[i]);
        }
      }
      return currentItems.slice(0, depth).concat(items).join('/');
    }

    function extractContentBrief(content) {
      var briefOffset = 512;
      var words = query.split(/\s+/g);
      var queryIndex = content.indexOf(words[0]);
      var briefContent;
      if (queryIndex > briefOffset) {
        return "..." + content.slice(queryIndex - briefOffset, queryIndex + briefOffset) + "...";
      } else if (queryIndex <= briefOffset) {
        return content.slice(0, queryIndex + briefOffset) + "...";
      }
    }

    function handleSearchResults(hits) {
      var numPerPage = 10;
      $('#pagination').empty();
      $('#pagination').removeData("twbs-pagination");
      if (hits.length === 0) {
        $('#search-results>.sr-items').html('<p>No results found</p>');
      } else {
        $('#pagination').twbsPagination({
          totalPages: Math.ceil(hits.length / numPerPage),
          visiblePages: 5,
          onPageClick: function (event, page) {
            var start = (page - 1) * numPerPage;
            var curHits = hits.slice(start, start + numPerPage);
            $('#search-results>.sr-items').empty().append(
              curHits.map(function (hit) {
                var currentUrl = window.location.href;
                var itemRawHref = relativeUrlToAbsoluteUrl(currentUrl, relHref + hit.href);
                var itemHref = relHref + hit.href + "?q=" + query;
                var itemTitle = hit.title;
                var itemBrief = extractContentBrief(hit.keywords);

                var itemNode = $('<div>').attr('class', 'sr-item');
                var itemTitleNode = $('<div>').attr('class', 'item-title').append($('<a>').attr('href', itemHref).attr("target", "_blank").text(itemTitle));
                var itemHrefNode = $('<div>').attr('class', 'item-href').text(itemRawHref);
                var itemBriefNode = $('<div>').attr('class', 'item-brief').text(itemBrief);
                itemNode.append(itemTitleNode).append(itemHrefNode).append(itemBriefNode);
                return itemNode;
              })
              );
            query.split(/\s+/).forEach(function (word) {
              if (word !== '') {
                highlight($('#search-results>.sr-items *'), word, "<strong>");
              }
            });
          }
        });
      }
    }
  })();

  // Update href in navbar
  (function () {
    var toc = $('#sidetoc');
    var breadcrumb = new Breadcrumb();
    loadNavbar();
    loadToc();
    function loadNavbar() {
      var navbarPath = $("meta[property='docfx\\:navrel']").attr("content");
      var tocPath = $("meta[property='docfx\\:tocrel']").attr("content");
      if (tocPath) tocPath = tocPath.replace(/\\/g, '/');
      if (navbarPath) navbarPath = navbarPath.replace(/\\/g, '/');
      $.get(navbarPath, function (data) {
        $(data).find("#toc>ul").appendTo("#navbar");
        if ($('#search-results').length !== 0) {
          $('#search').show();
          $('body').trigger("searchEvent");
        }
        var index = navbarPath.lastIndexOf('/');
        var navrel = '';
        if (index > -1) {
          navrel = navbarPath.substr(0, index + 1);
        }
        $('#navbar>ul').addClass('navbar-nav');
        var currentAbsPath = getAbsolutePath(window.location.pathname);
        // set active item
        $('#navbar').find('a[href]').each(function (i, e) {
          var href = $(e).attr("href");
          if (isRelativePath(href)) {
            href = navrel + href;
            $(e).attr("href", href);

            // TODO: currently only support one level navbar
            var isActive = false;
            var originalHref = e.name;
            if (originalHref) {
              originalHref = navrel + originalHref;
              if (getDirectory(getAbsolutePath(originalHref)) === getDirectory(getAbsolutePath(tocPath))) {
                isActive = true;
              }
            } else {
              if (getAbsolutePath(href) === currentAbsPath) {
                isActive = true;
              }
            }
            if (isActive) {
              $(e).parent().addClass(active);
              breadcrumb.insert({
                href: e.href,
                name: e.innerHTML
              }, 0);
            } else {
              $(e).parent().removeClass(active)
            }
          }
        });
      });
    }

    function loadToc() {
      var tocPath = $("meta[property='docfx\\:tocrel']").attr("content");
      if (tocPath) tocPath = tocPath.replace(/\\/g, '/');
      $('#sidetoc').load(tocPath + " #sidetoggle > div", function () {
        registerTocEvents();

        var index = tocPath.lastIndexOf('/');
        var tocrel = '';
        if (index > -1) {
          tocrel = tocPath.substr(0, index + 1);
        }
        var currentHref = getAbsolutePath(window.location.pathname);
        $('#sidetoc').find('a[href]').each(function (i, e) {
          var href = $(e).attr("href");
          if (isRelativePath(href)) {
            href = tocrel + href;
            $(e).attr("href", href);
          }

          if (getAbsolutePath(e.href) === currentHref) {
            $(e).parent().addClass(active);
            var parent = $(e).parent().parents('li').children('a');
            if (parent.length > 0) {
              parent.addClass(active);
              breadcrumb.push({
                href: parent[0].href,
                name: parent[0].innerHTML
              });
            }
            // for active li, expand it
            $(e).parents('ul.nav>li').addClass(expanded);

            breadcrumb.push({
              href: e.href,
              name: e.innerHTML
            });
            // Scroll to active item
            var top = 0;
            $(e).parents('li').each(function (i, e) {
              top += $(e).position().top;
            });
            // 50 is the size of the filter box
            $('.sidetoc').scrollTop(top - 50);
          } else {
            $(e).parent().removeClass(active);
            $(e).parents('li').children('a').removeClass(active);
          }
        });
      });
    }

    function registerTocEvents() {
      $('.toc .nav > li > .expand-stub').click(function (e) {
        $(e.target).parent().toggleClass(expanded);
      });
      $('.toc .nav > li > .expand-stub + a:not([href])').click(function (e) {
        $(e.target).parent().toggleClass(expanded);
      });
      $('#toc_filter_input').on('input', function (e) {
        var val = this.value;
        if (val === '') {
          // Clear 'filtered' class
          $('#toc li').removeClass(filtered).removeClass(hide);
          return;
        }

        // Get leaf nodes
        $('#toc li>a').filter(function (i, e) {
          return $(e).siblings().length === 0
        }).each(function (i, anchor) {
          var text = $(anchor).text();
          var parent = $(anchor).parent();
          var parentNodes = parent.parents('ul>li');
          for (var i = 0; i < parentNodes.length; i++) {
            var parentText = $(parentNodes[i]).children('a').text();
            if (parentText) text = parentText + '.' + text;
          };
          if (filterNavItem(text, val)) {
            parent.addClass(show);
            parent.removeClass(hide);
          } else {
            parent.addClass(hide);
            parent.removeClass(show);
          }
        });
        $('#toc li>a').filter(function (i, e) {
          return $(e).siblings().length > 0
        }).each(function (i, anchor) {
          var parent = $(anchor).parent();
          if (parent.find('li.show').length > 0) {
            parent.addClass(show);
            parent.addClass(filtered);
            parent.removeClass(hide);
          } else {
            parent.addClass(hide);
            parent.removeClass(show);
            parent.removeClass(filtered);
          }
        })

        function filterNavItem(name, text) {
          if (!text) return true;
          if (name.toLowerCase().indexOf(text.toLowerCase()) > -1) return true;
          return false;
        }
      });
    }

    function Breadcrumb() {
      var breadcrumb = [];
      this.push = pushBreadcrumb;
      this.insert = insertBreadcrumb;

      function pushBreadcrumb(obj) {
        breadcrumb.push(obj);
        setupBreadCrumb(breadcrumb);
      }

      function insertBreadcrumb(obj, index) {
        breadcrumb.splice(index, 0, obj);
        setupBreadCrumb(breadcrumb);
      }

      function setupBreadCrumb() {
        var html = formList(breadcrumb, 'breadcrumb');
        $('#breadcrumb').html(html);
      }
    }

    function getAbsolutePath(href) {
      // Use anchor to normalize href
      var anchor = $('<a href="' + href + '"></a>')[0];
      // Ignore protocal, remove search and query
      return anchor.host + anchor.pathname;
    }

    function isRelativePath(href) {
      return !isAbsolutePath(href);
    }

    function isAbsolutePath(href) {
      return (/^(?:[a-z]+:)?\/\//i).test(href);
    }

    function getDirectory(href) {
      if (!href) return '';
      var index = href.lastIndexOf('/');
      if (index == -1) return '';
      if (index > -1) {
        return href.substr(0, index);
      }
    }
  })();

  //Setup Affix
  (function () {
    var hierarchy = getHierarchy();
    if (hierarchy.length > 0) {
      var html = '<h5 class="title">In This Article</h5>'
      html += formList(hierarchy, ['nav', 'bs-docs-sidenav']);
      $("#affix").append(html);
      $('#affix').on('activate.bs.scrollspy', function (e) {
        if (e.target) {
            if ($(e.target).find('li.active').length > 0)
            {
              return;
            }
            var top = $(e.target).position().top;
            $(e.target).parents('li').each(function (i, e) {
              top += $(e).position().top;
            });
            var container = $('#affix > ul');
            container.scrollTop(container.scrollTop() + top - 100);
        }
      })
    }

    function getHierarchy() {
      // supported headers are h1, h2, h3, and h4
      // The topest header is ignored
      var selector = ".article article";
      var affixSelector = "#affix";
      var headers = ['h4', 'h3', 'h2', 'h1'];
      var hierarchy = [];
      var toppestIndex = -1;
      var startIndex = -1;
      // 1. get header hierarchy
      for (var i = headers.length - 1; i >= 0; i--) {
        var header = $(selector + " " + headers[i]);
        var length = header.length;

        // If contains no header in current selector, find the next one
        if (length === 0) continue;

        // If the toppest header contains only one item, e.g. title, ignore
        if (length === 1 && hierarchy.length === 0 && toppestIndex < 0) {
          toppestIndex = i;
          continue;
        }

        // Get second level children
        var nextLevelSelector = i > 0 ? headers[i - 1] : null;
        var prevSelector;
        for (var j = length - 1; j >= 0; j--) {
          var e = header[j];
          var id = e.id;
          if (!id) continue; // For affix, id is a must-have
          var item = {
            name: htmlEncode($(e).text()),
            href: "#" + id,
            items: []
          };
          if (nextLevelSelector) {
            var selector = '#' + id + "~" + nextLevelSelector;
            var currentSelector = selector;
            if (prevSelector) currentSelector += ":not(" + prevSelector + ")";
            $(header[j]).siblings(currentSelector).each(function (index, e) {
              if (e.id) {
                item.items.push({
                  name: htmlEncode($(e).text()), // innerText decodes text while innerHTML not
                  href: "#" + e.id

                })
              }
            })
            prevSelector = selector;
          }
          hierarchy.push(item);
        }
        break;
      };
      hierarchy.reverse();
      return hierarchy;
    }

    function htmlEncode(str) {
      return String(str)
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
    }

    function htmlDecode(value) {
      return String(value)
        .replace(/&quot;/g, '"')
        .replace(/&#39;/g, "'")
        .replace(/&lt;/g, '<')
        .replace(/&gt;/g, '>')
        .replace(/&amp;/g, '&');
    }
  })();

  function formList(item, classes) {
    var level = 1;
    var model = {
      items: item
    };
    var cls = [].concat(classes).join(" ");
    return getList(model, cls);

    function getList(model, cls) {
      if (!model || !model.items) return null;
      var l = model.items.length;
      if (l === 0) return null;
      var html = '<ul class="level' + level + ' ' + (cls || '') + '">';
      level++;
      for (var i = 0; i < l; i++) {
        var item = model.items[i];
        var href = item.href;
        var name = item.name;
        if (!name) continue;
        html += href ? '<li><a href="' + href + '">' + name + '</a>' : '<li>' + name;
        html += getList(item, cls) || '';
        html += '</li>';
      }
      html += '</ul>';
      return html;
    }
  }

  // For LOGO SVG
  // Replace SVG with inline SVG
  // http://stackoverflow.com/questions/11978995/how-to-change-color-of-svg-image-using-css-jquery-svg-image-replacement
  jQuery('img.svg').each(function () {
    var $img = jQuery(this);
    var imgID = $img.attr('id');
    var imgClass = $img.attr('class');
    var imgURL = $img.attr('src');

    jQuery.get(imgURL, function (data) {
      // Get the SVG tag, ignore the rest
      var $svg = jQuery(data).find('svg');

      // Add replaced image's ID to the new SVG
      if (typeof imgID !== 'undefined') {
        $svg = $svg.attr('id', imgID);
      }
      // Add replaced image's classes to the new SVG
      if (typeof imgClass !== 'undefined') {
        $svg = $svg.attr('class', imgClass + ' replaced-svg');
      }

      // Remove any invalid XML tags as per http://validator.w3.org
      $svg = $svg.removeAttr('xmlns:a');

      // Replace image with new SVG
      $img.replaceWith($svg);

    }, 'xml');
  });
})
