(function () {
    var rand = function () {
        return "?r=" + Math.random();
    }

    var hashtags = { "build": true, "lol": true, "fail": true };

    if (window && window.localStorage) {
        var values = window.localStorage.getItem("tweets");
        if (values) {
            hashtags = {};
            values.split(",").forEach(function (x) {
                hashtags[x] = true;
            });
        }
    }

    var updateDb = function () {
        if (window && window['localStorage']) {
            window.localStorage.setItem("tweets", Object.keys(hashtags).join(","));
        }
    }

    var removeHashtag = function (tag) {
        delete hashtags[tag];
        getScores();
        updateDb();
    }

    var addHashtag = function (tag) {
        if (tag === null || tag === "") return;

        tag = tag.replace("#").replace(/ /g, "").toLowerCase();
        hashtags[tag] = true;
        getScores();
        updateDb();
    }

    var template = Handlebars.compile($("#results-template").html());

    $("#add-button").click(function () {
        var value = $("#enter-hashtag").val();
        addHashtag(value);
        $("#enter-hashtag").val("");
    });

    $("#enter-hashtag").keyup(function (event) {
        if (event.keyCode == 13) {
            $("#add-button").click();
        }
    });

    var render = function (data) {

        data.totals.forEach(function (x) {
            if (x.LastUpdated === "\/Date(-62135596800000)\/") {
                x.Date = "-"
            } else {
                var date = new Date(parseInt(x.LastUpdated.substr(6)));
                x.Date = date.toLocaleDateString() + " " + date.toLocaleTimeString();
            }
            if (x.LastTweet == null || x.LastTweet == "") x.LastTweet = "-";
            if (x.Positive > x.Negative) x.Sentiment = "success";
            if (x.Positive < x.Negative) x.Sentiment = "danger";
        });
        $("#results-target").html(template(data));
        $("#results-target .remove").click(function (item) {
            removeHashtag(item.target.dataset.value);
        })
    }
    var getScores = function () {
        if (Object.keys(hashtags).length > 0) {
            $.get('/' + Object.keys(hashtags).join(',') + rand(), function (data) {
                render({ totals: data[0], counter: data[1] });
            });
        } else {
            render([]);
        }
    }
    setInterval(getScores, 5 * 1000);

    getScores();

})();