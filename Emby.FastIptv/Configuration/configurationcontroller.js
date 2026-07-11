define(['baseView', 'loading', 'emby-input', 'emby-button'], function (BaseView, loading) {
    'use strict';

    var pluginId = 'C7D8E9F0-A1B2-4C3D-8E4F-5A6B7C8D9E0F';

    function loadPage(view, config) {
        view.querySelector('#txtVideoCodec').value = config.DefaultVideoCodec || 'h264';
        view.querySelector('#txtAudioCodec').value = config.DefaultAudioCodec || 'aac';
        view.querySelector('#txtContainer').value = config.DefaultContainer || 'ts';
        view.querySelector('#txtWidth').value = config.DefaultWidth || 1920;
        view.querySelector('#txtHeight').value = config.DefaultHeight || 1080;
        view.querySelector('#txtUserAgent').value = config.UserAgent || '';
        view.querySelector('#txtCacheTtlHours').value = config.CacheTtlHours || 6;
        view.querySelector('#txtStreamTimeoutSeconds').value = config.StreamTimeoutSeconds || 15;
        view.querySelector('#txtStreamRetryCount').value = config.StreamRetryCount != null ? config.StreamRetryCount : 2;
        loading.hide();
    }

    function onSubmit(e) {
        e.preventDefault();
        loading.show();
        var form = this;
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.DefaultVideoCodec = form.querySelector('#txtVideoCodec').value || 'h264';
            config.DefaultAudioCodec = form.querySelector('#txtAudioCodec').value || 'aac';
            config.DefaultContainer = form.querySelector('#txtContainer').value || 'ts';
            config.DefaultWidth = parseInt(form.querySelector('#txtWidth').value, 10) || 1920;
            config.DefaultHeight = parseInt(form.querySelector('#txtHeight').value, 10) || 1080;
            config.UserAgent = form.querySelector('#txtUserAgent').value;
            config.CacheTtlHours = parseInt(form.querySelector('#txtCacheTtlHours').value, 10) || 6;
            config.StreamTimeoutSeconds = parseInt(form.querySelector('#txtStreamTimeoutSeconds').value, 10) || 15;
            config.StreamRetryCount = parseInt(form.querySelector('#txtStreamRetryCount').value, 10) || 0;
            return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then(function () {
            Dashboard.processPluginConfigurationUpdateResult();
        });
        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        view.querySelector('form').addEventListener('submit', onSubmit);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loading.show();
        var page = this.view;
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            loadPage(page, config);
        });
    };

    return View;
});
