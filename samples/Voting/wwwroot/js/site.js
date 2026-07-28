var app = angular.module('VotingApp', ['ui.bootstrap']);
app.run(function () { });

app.controller('VotingAppController', ['$rootScope', '$scope', '$http', '$timeout', function ($rootScope, $scope, $http, $timeout) {

    $scope.refresh = function () {
        start = new Date().getTime();
        $http.get('api/votes?c=' + new Date().getTime())
            .then((response) => {
                $scope.votes = response.data;
                end = new Date().getTime();
                updateFooter(response, (end - start));
            })
            .catch((error) => {
                updateFooter(error, 0);
            });
    }

    $scope.remove = function (item) {
        start = new Date().getTime();
        $http.delete('api/votes/' + item)
            .then(function (response) {
                $scope.refresh();
            })
            .catch((error) => {
                updateFooter(error, 0);
            });
    }

    $scope.add = function (item) {
        if (typeof item !== "undefined") {
            var fd = new FormData();
            fd.append('item', item);
            start = new Date().getTime();
            $http.put('api/votes/' + item, fd, {
                transformRequest: angular.identity,
                headers: { 'Content-Type': undefined }
            })
                .then(function (response) {
                    $scope.refresh();
                })
                .catch((error) => {
                    updateFooter(error, 0);
                });
        };
    }
}])

/*This function puts HTTP result in the footer */
function updateFooter(http, timeTaken) {
    if (http.status < 299) {
        statusText.innerHTML = 'Reponse:<br />HTTP status ' + http.status + ' ' + http.statusText + ' returned in ' + timeTaken.toString() + ' ms';
    }
    else {
        statusText.innerHTML = 'Error:<br /> An error occured';
    }
}